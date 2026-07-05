using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Dojo.STS2DojoCode.SeedSharing;
using SizeFlags = Godot.Control.SizeFlags;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// The Saved Fights half of <see cref="NDojoScreen"/> (§12e/§12g, UI-home decision 2026-07-04: a tab on
/// the Dojo screen, not a separate submenu): a paste box + explicit Submit button that imports an exported
/// fight, over a flat newest-first list of the library. Each row is fully expanded by default (no Load
/// toggle) and shows the fight's identity the same way the run browser does — a character avatar + ascension
/// badge, a boss/elite/monster avatar for the encounter, and the fight's relic bar — plus per-row actions:
/// Replay, Copy Code, Edit (title/description), and Delete (behind a confirmation modal). The most recently
/// imported row keeps a gold highlight border until the next successful action on the screen.
///
/// Deliberately a plain class owning script-less nodes (the §5m/§6 rule — mod classes deriving Godot
/// built-ins get broken script dispatch); the only scripted children are <see cref="DojoChip"/>s and the
/// two modal classes (<see cref="DojoConfirmModal"/>/<see cref="DojoEditFightModal"/>), all NButton- or
/// game-class-derived and in proven use.
/// </summary>
internal sealed class DojoSavedFightsView
{
    private const float RelicIconSize = 28f;

    public Control Root { get; }

    private readonly LineEdit _pasteBox;
    private readonly Label _statusLabel;
    private readonly ScrollContainer _scroll;
    private readonly VBoxContainer _rowContainer;

    private bool _importing;

    /// <summary>The full library, loaded once per <see cref="Refresh"/> (disk read) and filtered in memory
    /// on every filter change — a saved-fights library is small, but this avoids re-reading/re-parsing every
    /// file per keystroke. Each entry caches a lowercased search haystack (character/enemy/relic/title/seed).</summary>
    private readonly System.Collections.Generic.List<LoadedEntry> _entries = new();
    private int _unreadableFiles;

    private string _search = string.Empty;
    private ModelId? _characterFilter;
    private int? _ascensionFilter;

    private sealed record LoadedEntry(SavedFightEntry Entry, string Haystack);

    /// <summary>Path of the row to draw with a highlight border (the just-imported fight), and a live
    /// reference to its panel so a non-Refresh action can clear the border immediately. <c>_highlightedPath</c>
    /// is the source of truth that survives a rebuild (the panel node is rebuilt).</summary>
    private string? _highlightedPath;
    private PanelContainer? _highlightedPanel;

    public DojoSavedFightsView()
    {
        var root = new VBoxContainer();
        root.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddThemeConstantOverride("separation", 12);
        Root = root;

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        root.AddChild(header);
        header.AddChild(DojoUi.MakeLabel("Saved Fights", 26, StsColors.cream));

        // §12e was paste-to-import; now there's an explicit Submit button on the right (per request) so a
        // paste no longer fires validation on its own.
        var pasteRow = new HBoxContainer();
        pasteRow.AddThemeConstantOverride("separation", 10);
        root.AddChild(pasteRow);

        _pasteBox = new LineEdit();
        _pasteBox.PlaceholderText = "Paste the contents of an exported fight, then press Submit";
        _pasteBox.ClearButtonEnabled = true;
        _pasteBox.CustomMinimumSize = new Vector2(0, 46);
        _pasteBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _pasteBox.AddThemeStyleboxOverride("normal",
            NDojoScreen.MakePanelStyle(new Color("10131A"), NDojoScreen.RowBorderColor, 8));
        _pasteBox.AddThemeStyleboxOverride("focus",
            NDojoScreen.MakePanelStyle(new Color("10131A"), StsColors.gold, 8));
        _pasteBox.AddThemeColorOverride("font_color", StsColors.cream);
        _pasteBox.AddThemeColorOverride("font_placeholder_color", NDojoScreen.FaintText);
        _pasteBox.AddThemeFontSizeOverride("font_size", 16);
        if (DojoUi.UiFont is { } uiFont)
        {
            _pasteBox.AddThemeFontOverride("font", uiFont);
        }
        _pasteBox.TextSubmitted += _ => Submit();
        pasteRow.AddChild(_pasteBox);

        DojoChip submit = DojoUi.MakeChip("Submit", compact: false);
        submit.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        submit.Released += _ => Submit();
        pasteRow.AddChild(submit);

        _statusLabel = DojoUi.MakeLabel(string.Empty, 15, NDojoScreen.MutedText);
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(_statusLabel);

        _scroll = new ScrollContainer();
        _scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        root.AddChild(_scroll);

        _rowContainer = new VBoxContainer();
        _rowContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _rowContainer.AddThemeConstantOverride("separation", 12);
        _scroll.AddChild(_rowContainer);
    }

    // ------------------------------------------------------------------ data

    /// <summary>Re-reads the library from disk (rebuilding the cached entry+haystack list), then rebuilds
    /// the visible rows under the current filters.</summary>
    public void Refresh()
    {
        _entries.Clear();
        _unreadableFiles = 0;

        SavedFightListing listing;
        try
        {
            listing = DojoFightLibrary.List(
                SharedFightExporter.FightsDirectory, message => MainFile.Logger.Info(message));
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not list saved fights: " + e);
            ClearRows();
            SetStatus("Could not read the Saved Fights library — see log.", isError: true);
            return;
        }

        _unreadableFiles = listing.UnreadableFiles;
        foreach (SavedFightEntry entry in listing.Entries)
        {
            _entries.Add(new LoadedEntry(entry, BuildHaystack(entry.Payload)));
        }

        RebuildRows();
    }

    public void SetSearch(string? search)
    {
        _search = search ?? string.Empty;
        RebuildRows();
    }

    public void SetCharacterFilter(ModelId? characterId)
    {
        _characterFilter = characterId;
        RebuildRows();
    }

    public void SetAscensionFilter(int? ascension)
    {
        _ascensionFilter = ascension;
        RebuildRows();
    }

    public void ResetFilters()
    {
        _search = string.Empty;
        _characterFilter = null;
        _ascensionFilter = null;
        RebuildRows();
    }

    /// <summary>Rebuilds the visible rows from the cached library under the current filters, updating the
    /// status line. Cheap enough to run on every filter keystroke since it never touches disk.</summary>
    private void RebuildRows()
    {
        ClearRows();

        int shown = 0;
        foreach (LoadedEntry loaded in _entries)
        {
            if (!Matches(loaded))
            {
                continue;
            }
            _rowContainer.AddChild(BuildRow(loaded.Entry));
            shown++;
        }

        string summary;
        if (_entries.Count == 0)
        {
            summary = "No saved fights yet. Export one from a Dojo fight, or paste a fight code above.";
        }
        else if (shown == _entries.Count)
        {
            summary = $"{_entries.Count} saved fight{(_entries.Count == 1 ? "" : "s")}";
        }
        else
        {
            summary = $"{shown} of {_entries.Count} saved fights match";
        }
        if (_unreadableFiles > 0)
        {
            summary += $" · {_unreadableFiles} unreadable file{(_unreadableFiles == 1 ? "" : "s")} skipped";
        }
        SetStatus(summary, isError: false);
    }

    private void ClearRows()
    {
        _highlightedPanel = null; // rows are about to be freed; re-established by BuildRow if still visible
        foreach (Node child in _rowContainer.GetChildren())
        {
            _rowContainer.RemoveChild(child);
            child.QueueFreeSafely();
        }
        _scroll.ScrollVertical = 0;
    }

    private bool Matches(LoadedEntry loaded)
    {
        SharedFightPayload payload = loaded.Entry.Payload;
        if (_characterFilter != null && payload.CharacterId != _characterFilter)
        {
            return false;
        }
        if (_ascensionFilter != null && payload.Ascension != _ascensionFilter)
        {
            return false;
        }
        string query = _search.Trim().ToLowerInvariant();
        return query.Length == 0 || loaded.Haystack.Contains(query);
    }

    /// <summary>The lowercased searchable text for one fight — character/enemy/relic names plus title,
    /// comment and seed (matching the run browser's search surface). Precomputed at load time so a
    /// per-keystroke filter never re-resolves display names.</summary>
    private static string BuildHaystack(SharedFightPayload payload)
    {
        var text = new System.Text.StringBuilder();
        if (payload.CharacterId != null)
        {
            text.Append(DojoDisplayNames.Character(payload.CharacterId)).Append(' ');
        }
        if (payload.EncounterId != null)
        {
            text.Append(DojoDisplayNames.Encounter(payload.EncounterId)).Append(' ');
        }
        foreach (SerializableRelic relic in payload.Relics)
        {
            if (relic?.Id != null)
            {
                text.Append(DojoDisplayNames.Relic(relic.Id)).Append(' ');
            }
        }
        text.Append(payload.Title).Append(' ').Append(payload.Comment).Append(' ')
            .Append(payload.Author).Append(' ').Append(payload.Seed);
        return text.ToString().ToLowerInvariant();
    }

    private void Submit()
    {
        if (_importing)
        {
            return;
        }

        string pasted = _pasteBox.Text?.Trim() ?? string.Empty;
        if (pasted.Length == 0)
        {
            SetStatus("Paste an exported fight code first.", isError: true);
            return;
        }

        _importing = true;
        try
        {
            SharedFightPayload payload;
            try
            {
                payload = SharedFightCodec.Parse(pasted);
            }
            catch (SharedFightFormatException e)
            {
                SetStatus(e.Message, isError: true);
                return;
            }

            // Gate order per §12e: compatibility (build/schema) then content resolve; refusal messages
            // are already user-presentable.
            string? refusal = SharedFightLauncher.GetImportRefusal(payload);
            if (refusal != null)
            {
                SetStatus(refusal, isError: true);
                return;
            }

            string savedPath;
            try
            {
                savedPath = DojoFightLibrary.Save(SharedFightExporter.FightsDirectory, payload,
                    SavedFightOrigin.Imported, message => MainFile.Logger.Info(message));
            }
            catch (Exception e)
            {
                MainFile.Logger.Error("[STS2Dojo] Could not save the imported fight: " + e);
                SetStatus("Imported fight could not be saved to the library — see log.", isError: true);
                return;
            }

            _pasteBox.Text = string.Empty;
            _highlightedPath = savedPath; // BuildRow highlights the matching path on the Refresh below
            Refresh();
            SetStatus($"Imported '{payload.Title}' — highlighted below.", isError: false);
        }
        finally
        {
            _importing = false;
        }
    }

    private void SetStatus(string text, bool isError)
    {
        _statusLabel.Text = text;
        _statusLabel.AddThemeColorOverride(
            "font_color", isError ? new Color("E08A7A") : NDojoScreen.MutedText);
    }

    /// <summary>Drops the just-imported highlight (its border) the moment any other successful action
    /// completes on the screen. Clears both the persistent path and the live panel's border in place, so
    /// callers that don't rebuild the list (e.g. Copy Code) still visibly clear it.</summary>
    private void ClearHighlight()
    {
        if (_highlightedPanel != null && GodotObject.IsInstanceValid(_highlightedPanel))
        {
            _highlightedPanel.AddThemeStyleboxOverride(
                "panel", NDojoScreen.MakePanelStyle(NDojoScreen.RowColor, NDojoScreen.RowBorderColor, 10));
        }
        _highlightedPanel = null;
        _highlightedPath = null;
    }

    // ------------------------------------------------------------------ rows

    private Control BuildRow(SavedFightEntry entry)
    {
        SharedFightPayload payload = entry.Payload;
        bool highlighted = _highlightedPath != null && entry.FilePath == _highlightedPath;

        var rowPanel = new PanelContainer();
        StyleBoxFlat rowStyle = highlighted
            ? NDojoScreen.MakePanelStyle(NDojoScreen.RowColor, StsColors.gold, 10)
            : NDojoScreen.MakePanelStyle(NDojoScreen.RowColor, NDojoScreen.RowBorderColor, 10);
        if (highlighted)
        {
            rowStyle.SetBorderWidthAll(2);
            _highlightedPanel = rowPanel;
        }
        rowPanel.AddThemeStyleboxOverride("panel", rowStyle);
        rowPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 18);
        pad.AddThemeConstantOverride("margin_right", 18);
        pad.AddThemeConstantOverride("margin_top", 12);
        pad.AddThemeConstantOverride("margin_bottom", 12);
        rowPanel.AddChild(pad);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 16);
        columns.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        pad.AddChild(columns);

        columns.AddChild(BuildCharacterColumn(payload));
        columns.AddChild(BuildMonsterAvatar(payload));
        columns.AddChild(BuildCenterColumn(payload));
        columns.AddChild(BuildActionsColumn(entry, rowPanel));

        return rowPanel;
    }

    /// <summary>Character avatar + ascension badge, matching the run browser's badge column.</summary>
    private static Control BuildCharacterColumn(SharedFightPayload payload)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        box.SizeFlagsVertical = SizeFlags.ShrinkBegin;

        ModelId characterId = payload.CharacterId ?? ModelId.none;
        box.AddChild(DojoUi.MakeCharacterToken(characterId, ResolveCharacterColor(characterId)));

        Label ascension = DojoUi.MakeLabel($"A{payload.Ascension}", 15, StsColors.gold);
        ascension.HorizontalAlignment = HorizontalAlignment.Center;
        ascension.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddChild(ascension);
        return box;
    }

    private static Color ResolveCharacterColor(ModelId characterId)
    {
        try
        {
            CharacterModel? model = ModelDb.GetByIdOrNull<CharacterModel>(characterId);
            if (model != null)
            {
                // Same dim as the run rows — the raw name colors read too neon as a solid ring.
                return model.NameColor.Darkened(0.18f);
            }
        }
        catch (Exception)
        {
            // fall through to the neutral default
        }
        return StsColors.gray;
    }

    /// <summary>A boss/elite/monster avatar for the fight's encounter — the boss portrait when it's a boss,
    /// otherwise the elite/normal map icon, mirroring <see cref="DojoFightPill"/>'s icon resolution.</summary>
    private static Control BuildMonsterAvatar(SharedFightPayload payload)
    {
        var token = new PanelContainer();
        token.CustomMinimumSize = new Vector2(64, 64);
        token.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        token.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        token.MouseFilter = Control.MouseFilterEnum.Ignore;

        StyleBoxFlat style = NDojoScreen.MakePanelStyle(
            new Color("242B35"), NDojoScreen.RowBorderColor, 10);
        style.SetContentMarginAll(8);
        token.AddThemeStyleboxOverride("panel", style);

        Texture2D? icon = ResolveEncounterIcon(payload.EncounterId);
        if (icon != null)
        {
            var rect = new TextureRect();
            rect.Texture = icon;
            rect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            rect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            rect.MouseFilter = Control.MouseFilterEnum.Ignore;
            token.AddChild(rect);
        }
        else
        {
            Label q = DojoUi.MakeLabel("?", 26, NDojoScreen.MutedText);
            q.HorizontalAlignment = HorizontalAlignment.Center;
            q.VerticalAlignment = VerticalAlignment.Center;
            token.AddChild(q);
        }
        return token;
    }

    private static Texture2D? ResolveEncounterIcon(ModelId? encounterId)
    {
        if (encounterId == null)
        {
            return null;
        }
        try
        {
            RoomType roomType = ModelDb.GetByIdOrNull<EncounterModel>(encounterId)?.RoomType ?? RoomType.Monster;
            MapPointType mapPointType = roomType switch
            {
                RoomType.Boss => MapPointType.Boss,
                RoomType.Elite => MapPointType.Elite,
                _ => MapPointType.Monster
            };
            ModelId? iconModelId = roomType == RoomType.Boss ? encounterId : null;
            string? iconPath = ImageHelper.GetRoomIconPath(mapPointType, roomType, iconModelId);
            return iconPath != null ? PreloadManager.Cache.GetCompressedTexture2D(iconPath) : null;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info("[STS2Dojo] Could not resolve encounter icon for " + encounterId + ": " + e.Message);
            return null;
        }
    }

    private Control BuildCenterColumn(SharedFightPayload payload)
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        column.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // Fill the row's height (driven by the taller action column) so the expanding gaps below can
        // center the fight-data block between the subtitle and the row's bottom edge.
        column.SizeFlagsVertical = SizeFlags.ExpandFill;

        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 10);
        column.AddChild(titleRow);
        titleRow.AddChild(DojoUi.MakeLabel(payload.Title, 18, StsColors.cream));
        if (!string.IsNullOrWhiteSpace(payload.Author))
        {
            titleRow.AddChild(DojoUi.MakeLabel($"by {payload.Author}", 14, StsColors.gold));
        }

        if (!string.IsNullOrWhiteSpace(payload.Comment))
        {
            Label comment = DojoUi.MakeLabel(payload.Comment, 14, NDojoScreen.FaintText);
            comment.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            column.AddChild(comment);
        }

        // Equal expanding gaps above and below the fight-data block center it in the space left between
        // the subtitle and the row's bottom edge (the title/subtitle stay pinned to the top).
        column.AddChild(MakeVExpander());

        string character = payload.CharacterId != null
            ? DojoDisplayNames.Character(payload.CharacterId) : "?";
        string encounter = payload.EncounterId != null
            ? DojoDisplayNames.Encounter(payload.EncounterId) : "?";
        column.AddChild(DojoUi.MakeLabel(
            $"{character} · Ascension {payload.Ascension} · {encounter} · " +
            payload.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            14, NDojoScreen.MutedText));

        Control? relicBar = BuildRelicBar(payload);
        if (relicBar != null)
        {
            column.AddChild(relicBar);
        }

        // Always-loaded fight numbers (§12e's expanded state, now the default): the "Same seed every
        // attempt…" explainer line is deliberately dropped.
        column.AddChild(DojoUi.MakeLabel(
            $"HP {payload.CurrentHp}/{payload.MaxHp} · Gold {payload.Gold} · {payload.Deck.Count} cards · " +
            $"{payload.Relics.Count} relics · {payload.Potions.Count} potions · Seed {payload.Seed}",
            14, NDojoScreen.MutedText));

        column.AddChild(MakeVExpander());

        return column;
    }

    private static Control MakeVExpander() =>
        new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>The fight's relic bar — only the relics it carries entering that fight (payload.Relics IS
    /// the captured loadout, i.e. exactly "up until then"). Icons only, wrapped, unresolvable relics skipped
    /// (same quick-glance treatment as the run rows).</summary>
    private static Control? BuildRelicBar(SharedFightPayload payload)
    {
        if (payload.Relics.Count == 0)
        {
            return null;
        }

        var bar = new HFlowContainer();
        bar.AddThemeConstantOverride("h_separation", 6);
        bar.AddThemeConstantOverride("v_separation", 6);

        bool any = false;
        foreach (SerializableRelic relic in payload.Relics)
        {
            if (relic?.Id == null)
            {
                continue;
            }
            Texture2D? icon = DojoUi.ResolveRelicIconTexture(relic.Id);
            if (icon == null)
            {
                continue;
            }
            var rect = new TextureRect();
            rect.Texture = icon;
            rect.CustomMinimumSize = new Vector2(RelicIconSize, RelicIconSize);
            rect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            rect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            rect.MouseFilter = Control.MouseFilterEnum.Ignore;
            bar.AddChild(rect);
            any = true;
        }

        return any ? bar : null;
    }

    private Control BuildActionsColumn(SavedFightEntry entry, PanelContainer rowPanel)
    {
        SharedFightPayload payload = entry.Payload;

        var actions = new VBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        actions.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        actions.CustomMinimumSize = new Vector2(150, 0);

        // Gates re-checked at row-build time so a mod/content change mid-session is reflected.
        string? refusal = SharedFightLauncher.GetImportRefusal(payload);
        if (refusal == null)
        {
            DojoChip replay = DojoUi.MakeChip("Replay Fight", compact: false);
            replay.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            replay.Released += _ =>
            {
                ClearHighlight();
                TaskHelper.RunSafely(SharedFightLauncher.Launch(payload));
            };
            actions.AddChild(replay);
        }
        else
        {
            Label refusalLabel = DojoUi.MakeLabel(refusal, 13, new Color("E08A7A"));
            refusalLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            refusalLabel.CustomMinimumSize = new Vector2(150, 0);
            actions.AddChild(refusalLabel);
        }

        DojoChip copy = DojoUi.MakeChip("Copy Code", compact: true);
        copy.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        copy.Released += _ => OnCopyCode(payload);
        actions.AddChild(copy);

        DojoChip edit = DojoUi.MakeChip("Edit", compact: true);
        edit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        edit.Released += _ => OnEdit(entry);
        actions.AddChild(edit);

        DojoChip delete = DojoUi.MakeChip("Delete", compact: true);
        delete.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        delete.Released += _ => OnDelete(entry);
        actions.AddChild(delete);

        return actions;
    }

    // ------------------------------------------------------------------ per-row actions

    private void OnCopyCode(SharedFightPayload payload)
    {
        try
        {
            string code = SharedFightCodec.ToCode(payload);
            DisplayServer.ClipboardSet(code);
            ClearHighlight();
            SetStatus($"Copied '{payload.Title}' code to clipboard.", isError: false);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not copy fight code: " + e);
            SetStatus("Could not copy the fight code — see log.", isError: true);
        }
    }

    private void OnEdit(SavedFightEntry entry)
    {
        DojoEditFightModal.Open(entry.Payload.Title, entry.Payload.Comment, (title, description) =>
        {
            entry.Payload.Title = string.IsNullOrWhiteSpace(title) ? entry.Payload.Title : title;
            entry.Payload.Comment = description;
            try
            {
                DojoFightLibrary.Overwrite(entry.FilePath, entry.Payload, message => MainFile.Logger.Info(message));
            }
            catch (Exception e)
            {
                MainFile.Logger.Error("[STS2Dojo] Could not update saved fight: " + e);
                SetStatus("Could not save your edits — see log.", isError: true);
                return;
            }
            ClearHighlight();
            Refresh();
            SetStatus($"Updated '{entry.Payload.Title}'.", isError: false);
        });
    }

    private void OnDelete(SavedFightEntry entry)
    {
        DojoConfirmModal.Open(
            "Delete Saved Fight?",
            $"'{entry.Payload.Title}' will be permanently removed from your Saved Fights. This can't be undone.",
            "Delete",
            () =>
            {
                try
                {
                    DojoFightLibrary.Delete(entry.FilePath, message => MainFile.Logger.Info(message));
                }
                catch (Exception e)
                {
                    MainFile.Logger.Error("[STS2Dojo] Could not delete saved fight: " + e);
                    SetStatus("Could not delete the fight — see log.", isError: true);
                    return;
                }
                ClearHighlight();
                Refresh();
                SetStatus($"Deleted '{entry.Payload.Title}'.", isError: false);
            });
    }

}
