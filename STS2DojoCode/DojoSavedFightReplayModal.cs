using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Dojo.STS2DojoCode.SeedSharing;
using SizeFlags = Godot.Control.SizeFlags;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// The "Replay Fight" modal for a Saved Fights row: replaces the old direct
/// <see cref="SharedFightLauncher.Launch"/> call with a pre-launch screen offering the fight's
/// parameters. Two modes, chosen by a pair of mutually exclusive chips:
/// <list type="bullet">
/// <item><b>Play as Saved</b> (the default) — the exact captured replay: pinned seed + RNG counters +
/// captured relic/card state, byte-identical to what clicking Replay Fight did before this modal. All
/// parameter controls below are visibly present but dimmed and disabled, so the default reads as "these
/// exist, but you're not using them".</item>
/// <item><b>Customize</b> — enables the controls: a seed box (type one, or Randomize; a changed seed
/// still yields a fully repeatable fight, just not the original draws — see
/// <see cref="SavedFightCustomization"/>) and one state row per stateful relic/card in the loadout
/// (same stepper/preset language as <see cref="NDojoReplaySetupModal"/>), each seeded with the fight's
/// CAPTURED value rather than the spec default. Switching back to Play as Saved resets every control to
/// the captured values, so the exact mode is always honest.</item>
/// </list>
/// Start launches through the normal shared-fight pipeline; customizations are stamped onto a COPY of
/// the payload (the library entry is never mutated). Styled like the other Saved Fights modals
/// (<see cref="DojoConfirmModal"/>/<see cref="DojoEditFightModal"/>), not the stone Replay Setup panel —
/// this modal belongs to that family.
///
/// §5m (CLAUDE.md) applies: must be a node (<c>NModalContainer.Add</c> hard-casts to
/// <see cref="IScreenContext"/>), so nothing relies on lifecycle overrides on THIS class (built
/// imperatively from <see cref="Open"/>) and the root's mouse filter is Ignore with a script-less
/// full-rect Stop child terminating every native-tooltip walk.
/// </summary>
public partial class DojoSavedFightReplayModal : NTopBarPortrait, IScreenContext
{
    private const float PanelWidth = 800f;
    private const int SeedMaxLength = 25;

    private static readonly Color DisabledTint = new(1f, 1f, 1f, 0.4f);
    private static readonly Color ErrorText = new("E08A7A");
    private static readonly Color RowBg = new("1D242FEE");

    private SharedFightPayload _payload = null!;
    private readonly List<StateRow> _rows = new();
    private readonly List<NClickableControl> _optionControls = new();

    private DojoChip _exactChip = null!;
    private DojoChip _customChip = null!;
    private DojoChip? _startChip;
    private LineEdit _seedBox = null!;
    private Label _modeCaption = null!;
    private Label _statusLabel = null!;
    private Control _optionsBody = null!;
    private bool _customize;

    public Control? DefaultFocusedControl => _startChip;

    public static void Open(SharedFightPayload payload)
    {
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer == null || modalContainer.OpenModal != null)
        {
            MainFile.Logger.Error(
                "[STS2Dojo] Cannot open the Replay Fight modal: no modal container or one is already open.");
            return;
        }

        var modal = new DojoSavedFightReplayModal { _payload = payload };
        modal.BuildLayout();
        modalContainer.Add(modal);
        modal.SetCustomize(false);
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // §5m — keep this node out of every native-tooltip walk

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Stop;
        AddChild(center);

        var panel = new PanelContainer();
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(new Color("161A20F8"), StsColors.gold with { A = 0.6f }, 14);
        style.SetBorderWidthAll(2);
        panel.AddThemeStyleboxOverride("panel", style);
        panel.CustomMinimumSize = new Vector2(PanelWidth, 0);
        center.AddChild(panel);

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 28);
        pad.AddThemeConstantOverride("margin_right", 28);
        pad.AddThemeConstantOverride("margin_top", 24);
        pad.AddThemeConstantOverride("margin_bottom", 22);
        panel.AddChild(pad);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        pad.AddChild(column);

        // Header: what fight this is.
        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 10);
        column.AddChild(titleRow);
        titleRow.AddChild(DojoUi.MakeLabel("Replay Fight", 22, StsColors.cream));
        titleRow.AddChild(DojoUi.MakeLabel(_payload.Title, 16, StsColors.gold));

        string character = _payload.CharacterId != null
            ? DojoDisplayNames.Character(_payload.CharacterId) : "?";
        string encounter = _payload.EncounterId != null
            ? DojoDisplayNames.Encounter(_payload.EncounterId) : "?";
        column.AddChild(DojoUi.MakeLabel(
            $"{character} · Ascension {_payload.Ascension} · {encounter} · HP {_payload.CurrentHp}/{_payload.MaxHp}" +
            $" · Gold {_payload.Gold} · {_payload.Deck.Count} cards · {_payload.Relics.Count} relics",
            14, NDojoScreen.MutedText));

        // Mode choice — the explicit opt-in the parameter controls are gated behind.
        column.AddChild(MakeSectionHeader("REPLAY MODE"));

        var modeRow = new HBoxContainer();
        modeRow.AddThemeConstantOverride("separation", 10);
        column.AddChild(modeRow);

        _exactChip = DojoUi.MakeChip("Play as Saved", compact: true);
        _exactChip.Released += _ => SetCustomize(false);
        modeRow.AddChild(_exactChip);

        _customChip = DojoUi.MakeChip("Customize", compact: true);
        _customChip.Released += _ => SetCustomize(true);
        modeRow.AddChild(_customChip);

        _modeCaption = DojoUi.MakeLabel(string.Empty, 13, NDojoScreen.FaintText);
        _modeCaption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_modeCaption);

        // Everything below is the customizable parameter set — dimmed + disabled until Customize.
        var optionsBody = new VBoxContainer();
        optionsBody.AddThemeConstantOverride("separation", 10);
        _optionsBody = optionsBody;
        column.AddChild(optionsBody);

        BuildSeedSection(optionsBody);
        BuildStateSections(optionsBody);

        _statusLabel = DojoUi.MakeLabel(string.Empty, 14, ErrorText);
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_statusLabel);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        actions.Alignment = BoxContainer.AlignmentMode.End;
        actions.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        column.AddChild(actions);

        // DojoBackChip registers the cancel/back hotkeys so ESC closes the modal (CLAUDE.md §5i).
        var cancel = new DojoBackChip();
        cancel.Configure("Cancel", compact: false);
        cancel.Released += _ => NModalContainer.Instance?.Clear();
        actions.AddChild(cancel);

        _startChip = DojoUi.MakeChip("Start Replay", compact: false);
        _startChip.Released += _ => OnStart();
        actions.AddChild(_startChip);
    }

    private void BuildSeedSection(VBoxContainer optionsBody)
    {
        optionsBody.AddChild(MakeSectionHeader("SEED"));

        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 10);
        optionsBody.AddChild(seedRow);

        _seedBox = new LineEdit();
        _seedBox.Text = _payload.Seed;
        _seedBox.MaxLength = SeedMaxLength;
        _seedBox.CustomMinimumSize = new Vector2(0, 42);
        _seedBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _seedBox.AddThemeStyleboxOverride("normal",
            NDojoScreen.MakePanelStyle(new Color("10131A"), NDojoScreen.RowBorderColor, 8));
        _seedBox.AddThemeStyleboxOverride("focus",
            NDojoScreen.MakePanelStyle(new Color("10131A"), StsColors.gold, 8));
        _seedBox.AddThemeColorOverride("font_color", StsColors.cream);
        _seedBox.AddThemeFontSizeOverride("font_size", 16);
        if (DojoUi.UiFont is { } font)
        {
            _seedBox.AddThemeFontOverride("font", font);
        }
        seedRow.AddChild(_seedBox);

        seedRow.AddChild(MakeOptionPreset("Randomize",
            () => _seedBox.Text = SeedHelper.GetRandomSeed()));
        seedRow.AddChild(MakeOptionPreset("Saved Seed",
            () => _seedBox.Text = _payload.Seed));

        Label helper = DojoUi.MakeLabel(
            "The seed drives every draw, shuffle, and enemy intent. Any seed replays identically on " +
            "every attempt; a new seed rerolls the fight away from the original.",
            13, NDojoScreen.FaintText);
        helper.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        optionsBody.AddChild(helper);
    }

    /// <summary>RELIC STATE / CARD STATE rows for every stateful relic/card actually in the payload,
    /// initialized to the CAPTURED value (read out of the entry's Props; spec default when absent — that
    /// is what the live model holds when a property wasn't serialized). Sections render only when
    /// non-empty, in a fixed-height internal scroll when long.</summary>
    private void BuildStateSections(VBoxContainer optionsBody)
    {
        List<RowSource> relicRows = CollectRelicRows();
        List<RowSource> cardRows = CollectCardRows();

        if (relicRows.Count == 0 && cardRows.Count == 0)
        {
            optionsBody.AddChild(MakeSectionHeader("RELIC & CARD STATE"));
            optionsBody.AddChild(DojoUi.MakeLabel(
                "No relics or cards with adjustable state in this fight.", 13, NDojoScreen.FaintText));
            return;
        }

        var list = new VBoxContainer();
        list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        list.AddThemeConstantOverride("separation", 8);

        if (relicRows.Count > 0)
        {
            list.AddChild(MakeSectionHeader("RELIC STATE"));
            foreach (RowSource row in relicRows)
            {
                list.AddChild(BuildStateRow(row));
            }
        }
        if (cardRows.Count > 0)
        {
            list.AddChild(MakeSectionHeader("CARD STATE"));
            foreach (RowSource row in cardRows)
            {
                list.AddChild(BuildStateRow(row));
            }
        }

        int rowCount = relicRows.Count + cardRows.Count;
        if (rowCount <= 3)
        {
            optionsBody.AddChild(list);
            return;
        }

        var scroll = new ScrollContainer();
        scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.CustomMinimumSize = new Vector2(0, 300);
        scroll.AddChild(list);
        optionsBody.AddChild(scroll);
    }

    // ------------------------------------------------------------------ state rows

    /// <summary>Everything needed to render one row, resolved from the payload's DTOs.</summary>
    private sealed record RowSource(
        ModelId Id, int Occurrence, DojoStateSpec Spec, string Name, Texture2D? Icon, bool IsCard,
        int CapturedValue);

    private List<RowSource> CollectRelicRows()
    {
        var rows = new List<RowSource>();
        foreach (SerializableRelic relic in _payload.Relics)
        {
            if (relic?.Id == null)
            {
                continue;
            }
            RelicModel? model = SafeResolve(() => ModelDb.GetByIdOrNull<RelicModel>(relic.Id));
            if (model == null)
            {
                continue; // launch is gated on content resolve; no row is the safe answer
            }
            DojoStateSpec? spec = DojoStatefulContent.ForRelic(model);
            if (spec == null)
            {
                continue;
            }
            string name = SafeResolve(() => model.Title.GetFormattedText())
                ?? DojoDisplayNames.Prettify(relic.Id.Entry);
            Texture2D? icon = SafeResolve(() => model.Icon);
            rows.Add(new RowSource(relic.Id, 0, spec, name, icon, IsCard: false,
                CapturedValue: ReadCapturedValue(spec, relic.Props)));
        }
        return rows;
    }

    private List<RowSource> CollectCardRows()
    {
        var rows = new List<RowSource>();
        var occurrences = new Dictionary<ModelId, int>();
        foreach (SerializableCard card in _payload.Deck)
        {
            if (card?.Id == null)
            {
                continue;
            }
            occurrences.TryGetValue(card.Id, out int occurrence);
            occurrences[card.Id] = occurrence + 1;

            CardModel? model = SafeResolve(() => ModelDb.GetByIdOrNull<CardModel>(card.Id));
            if (model == null)
            {
                continue;
            }
            DojoStateSpec? spec = DojoStatefulContent.ForCard(model);
            if (spec == null)
            {
                continue;
            }

            string name = SafeResolve(() => model.TitleLocString.GetFormattedText())
                ?? DojoDisplayNames.Prettify(card.Id.Entry);
            if (card.CurrentUpgradeLevel == 1)
            {
                name += "+";
            }
            else if (card.CurrentUpgradeLevel > 1)
            {
                name += $"+{card.CurrentUpgradeLevel}";
            }
            if (occurrence > 0)
            {
                name += $" ({occurrence + 1})";
            }
            Texture2D? icon = SafeResolve(() => model.Portrait);
            rows.Add(new RowSource(card.Id, occurrence, spec, name, icon, IsCard: true,
                CapturedValue: ReadCapturedValue(spec, card.Props)));
        }
        return rows;
    }

    /// <summary>The fight's captured value for one spec — the props entry when present, else the spec
    /// default (a property at its initializer value isn't serialized). Clamped so an out-of-range
    /// capture still lands on a representable row value.</summary>
    private static int ReadCapturedValue(DojoStateSpec spec, SavedProperties? props)
    {
        if (spec.IsBool)
        {
            return SavedFightCustomization.TryGetBool(props, spec.PropertyName, out bool b)
                ? (b ? 1 : 0)
                : spec.Default;
        }
        return SavedFightCustomization.TryGetInt(props, spec.PropertyName, out int value)
            ? spec.Clamp(value)
            : spec.Default;
    }

    private static T? SafeResolve<T>(Func<T?> resolve) where T : class
    {
        try
        {
            return resolve();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Control BuildStateRow(RowSource source)
    {
        var state = new StateRow(source.Spec, source.Id, source.Occurrence, source.IsCard, source.CapturedValue);
        _rows.Add(state);

        var rowPanel = new PanelContainer();
        rowPanel.AddThemeStyleboxOverride("panel", NDojoScreen.MakePanelStyle(RowBg, NDojoScreen.RowBorderColor, 10));
        rowPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        rowPanel.AddChild(margin);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 14);
        margin.AddChild(columns);

        columns.AddChild(MakeStateIcon(source.Icon, source.Name, circular: !source.IsCard));

        var nameBox = new VBoxContainer();
        nameBox.AddThemeConstantOverride("separation", 2);
        nameBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameBox.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        nameBox.AddChild(DojoUi.MakeLabel(source.Name, 17, StsColors.cream));
        Label helper = DojoUi.MakeLabel(source.Spec.HelperText, 12, NDojoScreen.FaintText);
        helper.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        nameBox.AddChild(helper);
        columns.AddChild(nameBox);

        var controls = new VBoxContainer();
        controls.AddThemeConstantOverride("separation", 6);
        controls.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        controls.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        columns.AddChild(controls);

        var stepper = new HBoxContainer();
        stepper.AddThemeConstantOverride("separation", 8);
        stepper.Alignment = BoxContainer.AlignmentMode.End;
        controls.AddChild(stepper);

        var minus = new DojoStepperChip();
        minus.Configure("−");
        minus.Released += _ => state.Set(state.Value - 1);
        _optionControls.Add(minus);
        stepper.AddChild(minus);

        Label valueLabel = DojoUi.MakeLabel(string.Empty, 20, StsColors.cream);
        valueLabel.HorizontalAlignment = HorizontalAlignment.Center;
        valueLabel.VerticalAlignment = VerticalAlignment.Center;
        valueLabel.CustomMinimumSize = new Vector2(52, 0);
        state.ValueLabel = valueLabel;
        stepper.AddChild(valueLabel);

        var plus = new DojoStepperChip();
        plus.Configure("+");
        plus.Released += _ => state.Set(state.Value + 1);
        _optionControls.Add(plus);
        stepper.AddChild(plus);

        var presets = new HBoxContainer();
        presets.AddThemeConstantOverride("separation", 8);
        presets.Alignment = BoxContainer.AlignmentMode.End;
        controls.AddChild(presets);

        // Every row can jump back to the fight's captured value; relics keep the Replay Setup modal's
        // Zero/Random/Primed presets on top of that (cards carry only the stepper there, same here).
        presets.AddChild(MakeStatePreset(state, "Saved", () => state.Set(state.CapturedValue),
            activeWhen: value => value == state.CapturedValue));
        if (!source.IsCard)
        {
            presets.AddChild(MakeStatePreset(state, "Zero", () => state.Set(0),
                activeWhen: value => value == 0));
            presets.AddChild(MakeStatePreset(state, "Random",
                () => state.Set(Random.Shared.Next(source.Spec.Min, source.Spec.Max + 1)),
                activeWhen: null));
            if (source.Spec.Primed is int primed)
            {
                presets.AddChild(MakeStatePreset(state, "Primed", () => state.Set(primed),
                    activeWhen: value => value == primed));
            }
        }

        state.Set(source.CapturedValue);
        return rowPanel;
    }

    private DojoPresetChip MakeStatePreset(StateRow state, string text, Action apply, Func<int, bool>? activeWhen)
    {
        var chip = new DojoPresetChip();
        chip.Configure(text);
        chip.Released += _ => apply();
        state.Presets.Add((chip, activeWhen));
        _optionControls.Add(chip);
        return chip;
    }

    private DojoPresetChip MakeOptionPreset(string text, Action apply)
    {
        var chip = new DojoPresetChip();
        chip.Configure(text);
        chip.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        chip.Released += _ => apply();
        _optionControls.Add(chip);
        return chip;
    }

    private static Control MakeStateIcon(Texture2D? texture, string displayName, bool circular)
    {
        var frame = new PanelContainer();
        frame.CustomMinimumSize = new Vector2(46, 46);
        frame.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(
            new Color("151A22"), StsColors.gold with { A = 0.8f }, circular ? 23 : 8);
        style.SetBorderWidthAll(2);
        style.SetContentMarginAll(6);
        frame.AddThemeStyleboxOverride("panel", style);
        frame.MouseFilter = MouseFilterEnum.Ignore;

        if (texture != null)
        {
            var rect = new TextureRect();
            rect.Texture = texture;
            rect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            rect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            rect.MouseFilter = MouseFilterEnum.Ignore;
            frame.AddChild(rect);
        }
        else
        {
            Label initials = DojoUi.MakeLabel("?", 16, StsColors.gold);
            initials.HorizontalAlignment = HorizontalAlignment.Center;
            initials.VerticalAlignment = VerticalAlignment.Center;
            frame.AddChild(initials);
        }
        return frame;
    }

    private static Control MakeSectionHeader(string text)
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        header.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        header.AddChild(DojoUi.MakeLabel(text, 14, StsColors.gold));

        var rule = new ColorRect();
        rule.Color = StsColors.gold with { A = 0.3f };
        rule.CustomMinimumSize = new Vector2(0, 1);
        rule.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rule.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        header.AddChild(rule);
        return header;
    }

    // ------------------------------------------------------------------ mode + start

    /// <summary>The gray-out gate: Play as Saved keeps every parameter control visible but dimmed,
    /// disabled, and reset to the captured values; Customize lights them up.</summary>
    private void SetCustomize(bool customize)
    {
        _customize = customize;
        _exactChip.Selected = !customize;
        _customChip.Selected = customize;
        _modeCaption.Text = customize
            ? "Adjust the seed or relic/card state below. Every attempt still repeats your customized setup."
            : "Replays the fight exactly as captured — same seed, same draws, same relic and card state.";

        _optionsBody.Modulate = customize ? Colors.White : DisabledTint;
        foreach (NClickableControl control in _optionControls)
        {
            control.SetEnabled(customize);
        }
        _seedBox.Editable = customize;
        _seedBox.FocusMode = customize ? FocusModeEnum.All : FocusModeEnum.None;

        if (!customize)
        {
            _seedBox.Text = _payload.Seed;
            foreach (StateRow row in _rows)
            {
                row.Set(row.CapturedValue);
            }
        }
        _statusLabel.Text = string.Empty;
    }

    private void OnStart()
    {
        SharedFightPayload effective = _payload;
        if (_customize)
        {
            string seed = SeedHelper.CanonicalizeSeed(_seedBox.Text ?? string.Empty);
            if (seed.Length == 0)
            {
                _statusLabel.Text = "Enter a seed, or click Randomize.";
                return;
            }

            var customization = new SavedFightCustomization();
            if (seed != _payload.Seed)
            {
                customization.SeedOverride = seed;
            }
            foreach (StateRow row in _rows)
            {
                if (row.Value == row.CapturedValue)
                {
                    continue; // the captured value already rides in on the payload's own props
                }
                SavedProperties overlay = row.Spec.BuildProps(row.Value);
                if (row.IsCard)
                {
                    customization.SetCard(row.Id, row.Occurrence, overlay);
                }
                else
                {
                    customization.SetRelic(row.Id, overlay);
                }
            }
            if (customization.ChangesAnything(_payload))
            {
                effective = customization.BuildCustomizedPayload(_payload);
            }
        }

        NModalContainer.Instance?.Clear();
        TaskHelper.RunSafely(SharedFightLauncher.Launch(effective));
    }

    /// <summary>One row's live value + the widgets that reflect it (the Replay Setup modal's StateRow,
    /// plus the captured-value baseline this modal resets to and diffs against).</summary>
    private sealed class StateRow(DojoStateSpec spec, ModelId id, int occurrence, bool isCard, int capturedValue)
    {
        public DojoStateSpec Spec { get; } = spec;
        public ModelId Id { get; } = id;
        public int Occurrence { get; } = occurrence;
        public bool IsCard { get; } = isCard;
        public int CapturedValue { get; } = capturedValue;
        public int Value { get; private set; }
        public Label ValueLabel = null!;
        public readonly List<(DojoPresetChip Chip, Func<int, bool>? ActiveWhen)> Presets = new();

        public void Set(int value)
        {
            Value = Spec.Clamp(value);
            ValueLabel.Text = Spec.IsBool ? (Value != 0 ? "Yes" : "No") : Value.ToString();
            foreach ((DojoPresetChip chip, Func<int, bool>? activeWhen) in Presets)
            {
                chip.Selected = activeWhen?.Invoke(Value) ?? false;
            }
        }
    }
}
