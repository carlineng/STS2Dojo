using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using STS2Dojo.STS2DojoCode.SeedSharing;
using SizeFlags = Godot.Control.SizeFlags;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// The Saved Fights half of <see cref="NDojoScreen"/> (§12e/§12g, UI-home decision 2026-07-04: a tab on
/// the Dojo screen, not a separate submenu): a paste box that imports an exported fight the moment one
/// is pasted (no separate import button), over a flat newest-first list of the library — one row per
/// saved fight with title/character/ascension/encounter/date, a created-by-you vs imported badge, and
/// an in-place expandable Start/summary section (kept inline rather than a modal: consistent with the
/// run browser's expand-in-place philosophy, and §5m-safe).
///
/// Deliberately a plain class owning script-less nodes (the §5m/§6 rule — mod classes deriving Godot
/// built-ins get broken script dispatch); the only scripted children are <see cref="DojoChip"/>s, which
/// are already in proven use all over this screen. v1 keeps the list unfiltered (the sidebar's
/// character/ascension filters apply to runs only) — wiring them in is cheap follow-up work if saved
/// fights ever number enough to need it.
/// </summary>
internal sealed class DojoSavedFightsView
{
    private const int PasteAttemptMinLength = 12;

    public Control Root { get; }

    private readonly LineEdit _pasteBox;
    private readonly Label _statusLabel;
    private readonly ScrollContainer _scroll;
    private readonly VBoxContainer _rowContainer;
    private readonly Godot.Timer _pasteDebounce;

    private bool _importing;

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

        _pasteBox = new LineEdit();
        _pasteBox.PlaceholderText = "Paste the contents of an exported fight to replay it";
        _pasteBox.ClearButtonEnabled = true;
        _pasteBox.CustomMinimumSize = new Vector2(0, 46);
        _pasteBox.AddThemeStyleboxOverride("normal",
            NDojoScreen.MakePanelStyle(new Color("10131A"), NDojoScreen.RowBorderColor, 8));
        _pasteBox.AddThemeStyleboxOverride("focus",
            NDojoScreen.MakePanelStyle(new Color("10131A"), StsColors.gold, 8));
        _pasteBox.AddThemeColorOverride("font_color", StsColors.cream);
        _pasteBox.AddThemeColorOverride("font_placeholder_color", NDojoScreen.FaintText);
        _pasteBox.AddThemeFontSizeOverride("font_size", 16);
        Font? uiFont = DojoUi.UiFont;
        if (uiFont != null)
        {
            _pasteBox.AddThemeFontOverride("font", uiFont);
        }
        root.AddChild(_pasteBox);

        // §12e: pasting IS the import action. TextChanged also fires per keystroke, so imports are
        // debounced and short fragments are ignored silently instead of yelling "invalid" at someone
        // who typed two characters.
        _pasteDebounce = new Godot.Timer();
        _pasteDebounce.WaitTime = 0.35;
        _pasteDebounce.OneShot = true;
        _pasteDebounce.Timeout += () => TryImport(fromSubmit: false);
        root.AddChild(_pasteDebounce);
        _pasteBox.TextChanged += _ => _pasteDebounce.Start();
        _pasteBox.TextSubmitted += _ => TryImport(fromSubmit: true);

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

    public void Refresh()
    {
        foreach (Node child in _rowContainer.GetChildren())
        {
            _rowContainer.RemoveChild(child);
            child.QueueFreeSafely();
        }
        _scroll.ScrollVertical = 0;

        SavedFightListing listing;
        try
        {
            listing = DojoFightLibrary.List(
                SharedFightExporter.FightsDirectory, message => MainFile.Logger.Info(message));
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not list saved fights: " + e);
            SetStatus("Could not read the Saved Fights library — see log.", isError: true);
            return;
        }

        foreach (SavedFightEntry entry in listing.Entries)
        {
            _rowContainer.AddChild(BuildRow(entry));
        }

        string summary = listing.Entries.Count == 0
            ? "No saved fights yet. Export one from a Dojo fight, or paste a fight code above."
            : $"{listing.Entries.Count} saved fight{(listing.Entries.Count == 1 ? "" : "s")}";
        if (listing.UnreadableFiles > 0)
        {
            summary += $" · {listing.UnreadableFiles} unreadable file{(listing.UnreadableFiles == 1 ? "" : "s")} skipped";
        }
        SetStatus(summary, isError: false);
    }

    private void TryImport(bool fromSubmit)
    {
        if (_importing)
        {
            return;
        }

        string pasted = _pasteBox.Text?.Trim() ?? string.Empty;
        if (pasted.Length == 0 || (!fromSubmit && pasted.Length < PasteAttemptMinLength))
        {
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

            try
            {
                DojoFightLibrary.Save(SharedFightExporter.FightsDirectory, payload, SavedFightOrigin.Imported,
                    message => MainFile.Logger.Info(message));
            }
            catch (Exception e)
            {
                MainFile.Logger.Error("[STS2Dojo] Could not save the imported fight: " + e);
                SetStatus("Imported fight could not be saved to the library — see log.", isError: true);
                return;
            }

            _pasteBox.Text = string.Empty;
            Refresh();
            SetStatus($"Imported '{payload.Title}' — it's at the top of the list below.", isError: false);
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

    // ------------------------------------------------------------------ rows

    private Control BuildRow(SavedFightEntry entry)
    {
        SharedFightPayload payload = entry.Payload;

        var rowPanel = new PanelContainer();
        rowPanel.AddThemeStyleboxOverride(
            "panel", NDojoScreen.MakePanelStyle(NDojoScreen.RowColor, NDojoScreen.RowBorderColor, 10));
        rowPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 18);
        pad.AddThemeConstantOverride("margin_right", 18);
        pad.AddThemeConstantOverride("margin_top", 12);
        pad.AddThemeConstantOverride("margin_bottom", 12);
        rowPanel.AddChild(pad);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        pad.AddChild(column);

        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 10);
        column.AddChild(titleRow);

        titleRow.AddChild(DojoUi.MakeLabel(payload.Title, 18, StsColors.cream));
        titleRow.AddChild(MakeOriginBadge(entry.Origin));
        var filler = new Control();
        filler.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleRow.AddChild(filler);

        var loadChip = DojoUi.MakeChip("Load", compact: true);
        titleRow.AddChild(loadChip);

        string character = payload.CharacterId != null
            ? DojoDisplayNames.Character(payload.CharacterId) : "?";
        string encounter = payload.EncounterId != null
            ? DojoDisplayNames.Encounter(payload.EncounterId) : "?";
        column.AddChild(DojoUi.MakeLabel(
            $"{character} · Ascension {payload.Ascension} · {encounter} · " +
            payload.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            14, NDojoScreen.MutedText));

        if (!string.IsNullOrWhiteSpace(payload.Comment))
        {
            Label comment = DojoUi.MakeLabel(payload.Comment, 13, NDojoScreen.FaintText);
            comment.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            column.AddChild(comment);
        }

        // The expand-in-place confirm section (the §12e "preview → Start/Cancel", inline for v1): fight
        // numbers + Start, or the refusal text when the entry no longer passes the gates.
        VBoxContainer confirm = BuildConfirmSection(payload);
        confirm.Visible = false;
        column.AddChild(confirm);

        loadChip.Released += _ =>
        {
            confirm.Visible = !confirm.Visible;
            loadChip.Selected = confirm.Visible;
        };

        return rowPanel;
    }

    private static VBoxContainer BuildConfirmSection(SharedFightPayload payload)
    {
        var confirm = new VBoxContainer();
        confirm.AddThemeConstantOverride("separation", 8);

        confirm.AddChild(DojoUi.MakeLabel(
            $"HP {payload.CurrentHp}/{payload.MaxHp} · Gold {payload.Gold} · {payload.Deck.Count} cards · " +
            $"{payload.Relics.Count} relics · {payload.Potions.Count} potions · Seed {payload.Seed}",
            14, StsColors.cream));
        confirm.AddChild(DojoUi.MakeLabel(
            "Same seed every attempt — the opening hand and enemy actions repeat exactly, including Try Again.",
            13, NDojoScreen.FaintText));

        // Gates re-checked at expand time (not row-build time) so a mod/content change mid-session is
        // reflected the moment someone actually tries to use the entry.
        string? refusal = SharedFightLauncher.GetImportRefusal(payload);
        if (refusal != null)
        {
            Label refusalLabel = DojoUi.MakeLabel(refusal, 14, new Color("E08A7A"));
            refusalLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            confirm.AddChild(refusalLabel);
            return confirm;
        }

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 10);
        confirm.AddChild(actions);

        var startChip = DojoUi.MakeChip("Start Fight", compact: false);
        startChip.Released += _ => TaskHelper.RunSafely(SharedFightLauncher.Launch(payload));
        actions.AddChild(startChip);

        return confirm;
    }

    private static Control MakeOriginBadge(SavedFightOrigin origin)
    {
        bool created = origin == SavedFightOrigin.Created;
        Label badge = DojoUi.MakeLabel(created ? "BY YOU" : "IMPORTED", 12,
            created ? StsColors.gold : new Color("7FA8C9"));
        var frame = new PanelContainer();
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(
            new Color("10131A"),
            created ? StsColors.gold with { A = 0.6f } : new Color("7FA8C9") with { A = 0.6f },
            6);
        style.SetContentMarginAll(4);
        frame.AddThemeStyleboxOverride("panel", style);
        frame.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        frame.AddChild(badge);
        return frame;
    }
}
