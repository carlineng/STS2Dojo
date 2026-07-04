using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Runs;
using STS2Dojo.STS2DojoCode.Reconstruction;
using STS2Dojo.STS2DojoCode.SeedSharing;
using SizeFlags = Godot.Control.SizeFlags;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// The "Dojo — Replay Setup" modal: replaces the old one-line "Replay this fight?" yes/no popup
/// (<c>DojoReplayConfirmation</c>) with a single-screen setup panel. Top to bottom: gold "Dojo" title +
/// rule, a read-only summary strip (encounter, floor/character/ascension, HP/Gold/Potion chips built from
/// a cheap preview reconstruction — <see cref="DojoFloorEligibility.TryReconstructPreview"/>), an
/// explainer line, then RELIC STATE / CARD STATE sections listing one row per stateful relic/card
/// actually in the reconstructed loadout (<see cref="DojoStatefulContent"/>), each with a ∓ stepper and —
/// for relics — Zero/Random/Primed presets. Cancel/Start are the stock red/green ribbon buttons
/// (<see cref="NPopupYesNoButton"/>, duplicated out of the game's generic popup scene), so ESC cancels
/// and confirm starts, exactly like the popup this replaces. Start collects the tuned values into a
/// <see cref="DojoStateAdjustments"/> and launches through <see cref="DojoReplayLauncher"/>.
///
/// Defaults match what a launch does today with no modal (the reconstructor deliberately drops per-floor
/// counter props as unrecoverable — CLAUDE.md §5), so a player can always just hit Start.
///
/// Visual chrome mirrors <see cref="DojoCompletionScreen"/>: the painterly stone panel is the rewards
/// screen's "Rewards" frame extracted off-tree, the gold serif title is a duplicate of the generic
/// popup's header label, and everything else is code-built with the Dojo screen's flat styles.
///
/// §5m (CLAUDE.md) applies exactly as it does to <see cref="DojoCompletionScreen"/>: this class must be a
/// node (<c>NModalContainer.Add</c> hard-casts to <see cref="IScreenContext"/>) and its script dispatch
/// may be broken in the modded game, so (a) nothing here relies on lifecycle overrides on THIS class —
/// building and wiring are driven imperatively from <see cref="Open"/> — and (b) the root's mouse filter
/// is Ignore with a script-less full-rect Stop child terminating every native-tooltip walk before it can
/// ascend into this node.
/// </summary>
public partial class NDojoReplaySetupModal : NTopBarPortrait, IScreenContext
{
    private const float PanelWidth = 880f;
    private const float MinPanelHeight = 620f;
    private const float MaxPanelHeight = 980f;
    private const int DuplicateFlagsNoSignals = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts);

    private static readonly Color PanelFallbackBg = new("1A1F27F8");
    private static readonly Color InsetBg = new("10141BE6");
    private static readonly Color InsetBorder = new("2A313B");
    private static readonly Color RowBg = new("1D242FEE");
    private static readonly Color MutedText = new("9AA3AE");
    private static readonly Color FaintText = new("6B7480");
    private static readonly Color OutlineDark = new("0C0F14");

    /// <summary>The game's real italic font, resolved from the popup Description label's theme
    /// ("italics_font" — the font MegaRichTextLabel uses for [i] spans) while the popup template is in
    /// hand. Null if the theme doesn't define one; the explainer then falls back to a faux shear.</summary>
    private static Font? _italicFont;

    private RunHistory _history = null!;
    private int _globalFloor;
    private readonly List<StateRow> _rows = new();
    private NPopupYesNoButton? _startButton;
    private NPopupYesNoButton? _cancelButton;
    private DojoPresetChip? _exportChip;
    private Label? _exportChipLabel;
    private bool _exportInFlight;

    public Control? DefaultFocusedControl => _startButton;

    /// <summary>Builds and shows the modal for one combat floor. Callers have already gated on
    /// <see cref="DojoFloorEligibility.IsEligible"/>, so a failed preview here is unexpected — it logs
    /// and shows nothing rather than crashing or launching blind.</summary>
    public static void Open(RunHistory history, int globalFloor)
    {
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer == null || modalContainer.OpenModal != null)
        {
            MainFile.Logger.Error("[STS2Dojo] Cannot open Replay Setup: no modal container or one is already open.");
            return;
        }

        ReconstructedLoadout? preview = DojoFloorEligibility.TryReconstructPreview(history, globalFloor);
        if (preview == null)
        {
            return; // TryReconstructPreview already logged.
        }

        var modal = new NDojoReplaySetupModal { _history = history, _globalFloor = globalFloor };
        if (!modal.BuildLayout(preview))
        {
            // Never added to a tree, so it must be freed explicitly or it leaks.
            modal.QueueFreeSafely();
            return;
        }

        modalContainer.Add(modal);
        modal.WireActions();
    }

    // ------------------------------------------------------------------ layout

    private bool BuildLayout(ReconstructedLoadout preview)
    {
        List<RowModel> relicRows = CollectRelicRows(preview);
        List<RowModel> cardRows = CollectCardRows(preview);

        if (!TryExtractPopupPieces(out MegaLabel? title, out NPopupYesNoButton? start, out NPopupYesNoButton? cancel,
                out Vector2 buttonSize))
        {
            MainFile.Logger.Error("[STS2Dojo] Could not extract the game's popup pieces for the Replay Setup modal.");
            return false;
        }
        _startButton = start;
        _cancelButton = cancel;

        SetAnchorsPreset(LayoutPreset.FullRect);
        // §5m: this node must be unreachable by the native tooltip walk — root Ignore, and the script-less
        // full-rect CenterContainer below is the Stop control that both swallows modal input and
        // terminates every walk (see DojoCompletionScreen.BuildLayout for the full explanation).
        MouseFilter = MouseFilterEnum.Ignore;

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Stop;
        AddChild(center);

        float panelHeight = ComputePanelHeight(relicRows.Count, cardRows.Count, buttonSize.Y);
        Control panel = TryBuildStonePanel(panelHeight) ?? BuildPlainPanel(panelHeight);
        center.AddChild(panel);

        ConfigureTitle(title!);
        panel.AddChild(title!);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 46);
        margin.AddThemeConstantOverride("margin_right", 46);
        margin.AddThemeConstantOverride("margin_top", 98);
        margin.AddThemeConstantOverride("margin_bottom", 30);
        panel.AddChild(margin);

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 12);
        margin.AddChild(content);

        content.AddChild(MakeTitleRule());
        content.AddChild(BuildSummaryStrip(preview));
        content.AddChild(MakeExplainer());
        content.AddChild(BuildStateScroll(relicRows, cardRows));
        content.AddChild(BuildActionBar(buttonSize));

        return true;
    }

    private static float ComputePanelHeight(int relicRows, int cardRows, float buttonHeight)
    {
        // Fixed chrome: top margin + rule + summary strip + explainer + action bar + bottom margin +
        // VBox separations. Rows: header + row heights per section. Clamped so long lists scroll
        // internally while short ones don't leave a cavern of empty stone.
        float chrome = 98 + 14 + 96 + 26 + Math.Max(buttonHeight, 96f) + 30 + 60;
        float rows = 34 + Math.Max(relicRows, 1) * 102;
        if (cardRows > 0)
        {
            rows += 34 + cardRows * 80;
        }
        return Mathf.Clamp(chrome + rows, MinPanelHeight, MaxPanelHeight);
    }

    /// <summary>Best-effort reuse of the rewards screen's stone "Rewards" frame — the same painterly
    /// torn-edge panel (and the same extraction) <see cref="DojoCompletionScreen"/> ships with. Null on
    /// any failure so the caller falls back to a flat panel.</summary>
    private static Control? TryBuildStonePanel(float panelHeight)
    {
        Control? panel = null;
        try
        {
            string scenePath = SceneHelper.GetScenePath("screens/rewards_screen");
            var templateScreen = PreloadManager.Cache.GetScene(scenePath).Instantiate<NRewardsScreen>();
            panel = templateScreen.GetNode<Control>("Rewards");
            panel.GetParent().RemoveChild(panel);
            templateScreen.QueueFreeSafely();

            foreach (Node child in panel.GetChildren())
            {
                panel.RemoveChild(child);
                child.QueueFreeSafely();
            }
            DojoNodeDuplication.ReownRecursively(panel);

            panel.Modulate = Colors.White;
            panel.CustomMinimumSize = new Vector2(PanelWidth, panelHeight);
            panel.AnchorLeft = 0f;
            panel.AnchorTop = 0f;
            panel.AnchorRight = 0f;
            panel.AnchorBottom = 0f;
            panel.OffsetLeft = 0f;
            panel.OffsetTop = 0f;
            panel.OffsetRight = PanelWidth;
            panel.OffsetBottom = panelHeight;
            panel.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            panel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            panel.PivotOffset = new Vector2(PanelWidth / 2f, panelHeight / 2f);
            return panel;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not reuse rewards-screen chrome for the Replay Setup " +
                "modal, falling back to a flat panel: " + e);
            panel?.QueueFreeSafely();
            return null;
        }
    }

    private static Control BuildPlainPanel(float panelHeight)
    {
        // Plain Control (not PanelContainer — it would override the title/margin anchors) with a
        // full-rect styled backdrop child.
        var panel = new Control();
        panel.CustomMinimumSize = new Vector2(PanelWidth, panelHeight);
        panel.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        panel.SizeFlagsVertical = SizeFlags.ShrinkCenter;

        var backdrop = new PanelContainer();
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(PanelFallbackBg, InsetBorder, 14);
        style.SetBorderWidthAll(2);
        backdrop.AddThemeStyleboxOverride("panel", style);
        backdrop.MouseFilter = MouseFilterEnum.Ignore;
        panel.AddChild(backdrop);
        return panel;
    }

    /// <summary>Duplicates the pieces of the game's generic popup this modal reuses: the gold serif
    /// header label (the title) and the red/green ribbon yes/no buttons — unchanged shapes, per the mock.
    /// Everything is re-owned (unique-name lookups in their _Ready — see DojoNodeDuplication) and each
    /// button's Image/Outline materials are made unique: the focus tint tweens shader parameters on the
    /// material, and a shared material instance would tint every button using it at once (the
    /// completion-screen flicker bug).</summary>
    private static bool TryExtractPopupPieces(
        out MegaLabel? title, out NPopupYesNoButton? start, out NPopupYesNoButton? cancel, out Vector2 buttonSize)
    {
        title = null;
        start = null;
        cancel = null;
        buttonSize = new Vector2(300, 110);

        NGenericPopup? popup = NGenericPopup.Create();
        if (popup == null)
        {
            return false;
        }

        try
        {
            var headerTemplate = popup.GetNode<MegaLabel>("VerticalPopup/Header");
            var yesTemplate = popup.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton");
            var noTemplate = popup.GetNode<NPopupYesNoButton>("VerticalPopup/NoButton");
            ResolveItalicFont(popup);
            if (yesTemplate.Size.X > 1f && yesTemplate.Size.Y > 1f)
            {
                buttonSize = yesTemplate.Size;
            }

            title = (MegaLabel)headerTemplate.Duplicate(DuplicateFlagsNoSignals);
            DojoNodeDuplication.ReownRecursively(title);

            start = (NPopupYesNoButton)yesTemplate.Duplicate(DuplicateFlagsNoSignals);
            DojoNodeDuplication.ReownRecursively(start);
            MakeButtonMaterialsUnique(start);

            cancel = (NPopupYesNoButton)noTemplate.Duplicate(DuplicateFlagsNoSignals);
            DojoNodeDuplication.ReownRecursively(cancel);
            MakeButtonMaterialsUnique(cancel);

            return true;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not duplicate popup header/buttons: " + e);
            title?.QueueFreeSafely();
            start?.QueueFreeSafely();
            cancel?.QueueFreeSafely();
            title = null;
            start = null;
            cancel = null;
            return false;
        }
        finally
        {
            popup.QueueFreeSafely();
        }
    }

    private static void ResolveItalicFont(NGenericPopup popup)
    {
        if (_italicFont != null)
        {
            return;
        }
        try
        {
            var description = popup.GetNode<Control>("VerticalPopup/Description");
            if (description.HasThemeFont(ThemeConstants.RichTextLabel.ItalicsFont, "RichTextLabel"))
            {
                _italicFont = description.GetThemeFont(ThemeConstants.RichTextLabel.ItalicsFont, "RichTextLabel");
            }
        }
        catch (Exception)
        {
            // Non-fatal: the explainer falls back to a sheared regular font.
        }
    }

    private static void MakeButtonMaterialsUnique(Node button)
    {
        foreach (string name in new[] { "Image", "Outline" })
        {
            if (button.FindChild(name, recursive: true, owned: false) is CanvasItem item &&
                item.Material != null)
            {
                item.Material = (Material)item.Material.Duplicate();
            }
        }
    }

    private static void ConfigureTitle(MegaLabel title)
    {
        title.Name = "DojoTitle";
        title.Visible = true;
        title.Modulate = Colors.White;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.MouseFilter = MouseFilterEnum.Ignore;
        title.AnchorLeft = 0f;
        title.AnchorRight = 1f;
        title.AnchorTop = 0f;
        title.AnchorBottom = 0f;
        title.OffsetLeft = 0f;
        title.OffsetRight = 0f;
        title.OffsetTop = 28f;
        title.OffsetBottom = 86f;
        title.SetTextAutoSize("Dojo");
    }

    private static Control MakeTitleRule()
    {
        var host = new CenterContainer();
        var rule = new ColorRect();
        rule.Color = StsColors.gold with { A = 0.55f };
        rule.CustomMinimumSize = new Vector2(220, 2);
        host.AddChild(rule);
        return host;
    }

    // ------------------------------------------------------------------ summary strip

    private Control BuildSummaryStrip(ReconstructedLoadout preview)
    {
        var strip = new PanelContainer();
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(InsetBg, InsetBorder, 10);
        style.SetContentMarginAll(14);
        strip.AddThemeStyleboxOverride("panel", style);
        strip.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 14);
        strip.AddChild(columns);

        var identity = new VBoxContainer();
        identity.AddThemeConstantOverride("separation", 3);
        identity.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        identity.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        columns.AddChild(identity);

        identity.AddChild(MakeOutlinedLabel(DojoDisplayNames.Encounter(preview.EncounterId), 24, StsColors.cream));

        var subline = new HBoxContainer();
        subline.AddThemeConstantOverride("separation", 8);
        subline.AddChild(DojoUi.MakeLabel($"Floor {_globalFloor}", 15, MutedText));
        subline.AddChild(DojoUi.MakeLabel("·", 15, FaintText));
        subline.AddChild(DojoUi.MakeLabel(DojoDisplayNames.Character(preview.CharacterId), 15, MutedText));
        subline.AddChild(DojoUi.MakeLabel("·", 15, FaintText));
        subline.AddChild(DojoUi.MakeLabel($"Ascension {preview.Ascension}", 15, StsColors.gold));
        identity.AddChild(subline);

        var chips = new HBoxContainer();
        chips.AddThemeConstantOverride("separation", 8);
        chips.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        chips.AddChild(MakeStatChip("HP", $"{preview.CurrentHp}/{preview.MaxHp}", StsColors.red));
        chips.AddChild(MakeStatChip("GOLD", preview.Gold.ToString(), StsColors.gold));
        chips.AddChild(MakeStatChip("POTIONS", $"{preview.Potions.Count}/{preview.MaxPotionSlots}", StsColors.cream));
        columns.AddChild(chips);

        return strip;
    }

    private static Control MakeStatChip(string caption, string value, Color valueColor)
    {
        var chip = new PanelContainer();
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(new Color("161B23"), InsetBorder, 8);
        style.SetContentMarginAll(8);
        chip.AddThemeStyleboxOverride("panel", style);
        chip.CustomMinimumSize = new Vector2(86, 0);

        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 1);
        chip.AddChild(stack);

        Label captionLabel = DojoUi.MakeLabel(caption, 11, FaintText);
        captionLabel.HorizontalAlignment = HorizontalAlignment.Center;
        stack.AddChild(captionLabel);

        Label valueLabel = MakeOutlinedLabel(value, 17, valueColor);
        valueLabel.HorizontalAlignment = HorizontalAlignment.Center;
        stack.AddChild(valueLabel);
        return chip;
    }

    private static Control MakeExplainer()
    {
        Label label = DojoUi.MakeLabel(
            "Some fight state can't be recovered exactly. Adjust anything you want before starting.",
            14, MutedText);
        if (_italicFont != null)
        {
            label.AddThemeFontOverride("font", _italicFont);
        }
        else if (DojoUi.UiFont is { } baseFont)
        {
            // Faux italic fallback: shear the regular UI font. The negative Y-axis x-component leans
            // glyph tops (negative y relative to the baseline) to the right.
            var italic = new FontVariation();
            italic.BaseFont = baseFont;
            italic.VariationTransform = new Transform2D(new Vector2(1f, 0f), new Vector2(-0.18f, 1f), Vector2.Zero);
            label.AddThemeFontOverride("font", italic);
        }
        return label;
    }

    // ------------------------------------------------------------------ state rows

    /// <summary>Everything needed to render one row, resolved from the preview loadout.</summary>
    private sealed record RowModel(
        ModelId Id, int Occurrence, DojoStateSpec Spec, string Name, Texture2D? Icon, bool IsCard);

    private static List<RowModel> CollectRelicRows(ReconstructedLoadout preview)
    {
        var rows = new List<RowModel>();
        foreach (ProvenancedRelic pr in preview.Relics)
        {
            if (pr.Relic.Id == null)
            {
                continue;
            }
            RelicModel? model = SafeResolve(() => ModelDb.GetByIdOrNull<RelicModel>(pr.Relic.Id));
            if (model == null)
            {
                continue; // eligibility already passed, so this shouldn't happen; no row is the safe answer
            }
            DojoStateSpec? spec = DojoStatefulContent.ForRelic(model);
            if (spec == null)
            {
                continue;
            }
            string name = SafeResolve(() => model.Title.GetFormattedText())
                ?? DojoDisplayNames.Prettify(pr.Relic.Id.Entry);
            Texture2D? icon = SafeResolve(() => model.Icon);
            rows.Add(new RowModel(pr.Relic.Id, 0, spec, name, icon, IsCard: false));
        }
        return rows;
    }

    private static List<RowModel> CollectCardRows(ReconstructedLoadout preview)
    {
        var rows = new List<RowModel>();
        var occurrences = new Dictionary<ModelId, int>();
        foreach (ProvenancedCard pc in preview.Deck)
        {
            if (pc.Card.Id == null)
            {
                continue;
            }
            occurrences.TryGetValue(pc.Card.Id, out int occurrence);
            occurrences[pc.Card.Id] = occurrence + 1;

            CardModel? model = SafeResolve(() => ModelDb.GetByIdOrNull<CardModel>(pc.Card.Id));
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
                ?? DojoDisplayNames.Prettify(pc.Card.Id.Entry);
            if (pc.Card.CurrentUpgradeLevel == 1)
            {
                name += "+";
            }
            else if (pc.Card.CurrentUpgradeLevel > 1)
            {
                name += $"+{pc.Card.CurrentUpgradeLevel}";
            }
            if (occurrence > 0)
            {
                name += $" ({occurrence + 1})"; // 2nd, 3rd copy of the same card, each with its own row
            }
            Texture2D? icon = SafeResolve(() => model.Portrait);
            rows.Add(new RowModel(pc.Card.Id, occurrence, spec, name, icon, IsCard: true));
        }
        return rows;
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

    private Control BuildStateScroll(List<RowModel> relicRows, List<RowModel> cardRows)
    {
        var scroll = new ScrollContainer();
        scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;

        var list = new VBoxContainer();
        list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        list.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(list);

        list.AddChild(MakeSectionHeader("RELIC STATE"));
        if (relicRows.Count == 0)
        {
            list.AddChild(DojoUi.MakeLabel("No relics with hidden state in this loadout.", 14, FaintText));
        }
        foreach (RowModel row in relicRows)
        {
            list.AddChild(BuildStateRow(row));
        }

        // Per the mock: the card section renders only when the deck actually holds a stateful card.
        if (cardRows.Count > 0)
        {
            list.AddChild(MakeSpacer(4));
            list.AddChild(MakeSectionHeader("CARD STATE"));
            foreach (RowModel row in cardRows)
            {
                list.AddChild(BuildStateRow(row));
            }
        }

        return scroll;
    }

    private static Control MakeSectionHeader(string text)
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        header.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        header.AddChild(DojoUi.MakeLabel(text, 15, StsColors.gold));

        var rule = new ColorRect();
        rule.Color = StsColors.gold with { A = 0.3f };
        rule.CustomMinimumSize = new Vector2(0, 1);
        rule.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rule.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        header.AddChild(rule);
        return header;
    }

    private static Control MakeSpacer(float height) => new Control { CustomMinimumSize = new Vector2(0, height) };

    private Control BuildStateRow(RowModel model)
    {
        var state = new StateRow(model.Spec, model.Id, model.Occurrence, model.IsCard);
        _rows.Add(state);

        var rowPanel = new PanelContainer();
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(RowBg, InsetBorder, 10);
        style.SetContentMarginAll(0);
        rowPanel.AddThemeStyleboxOverride("panel", style);
        rowPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        rowPanel.AddChild(margin);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 16);
        margin.AddChild(columns);

        columns.AddChild(MakeStateIcon(model.Icon, model.Name, circular: !model.IsCard));

        var nameBox = new VBoxContainer();
        nameBox.AddThemeConstantOverride("separation", 2);
        nameBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameBox.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        nameBox.AddChild(MakeOutlinedLabel(model.Name, 19, StsColors.cream));
        nameBox.AddChild(DojoUi.MakeLabel(model.Spec.HelperText, 13, FaintText));
        columns.AddChild(nameBox);

        var controls = new VBoxContainer();
        controls.AddThemeConstantOverride("separation", 7);
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
        stepper.AddChild(minus);

        Label valueLabel = MakeOutlinedLabel(string.Empty, 24, StsColors.cream);
        valueLabel.HorizontalAlignment = HorizontalAlignment.Center;
        valueLabel.VerticalAlignment = VerticalAlignment.Center;
        valueLabel.CustomMinimumSize = new Vector2(56, 0);
        state.ValueLabel = valueLabel;
        stepper.AddChild(valueLabel);

        var plus = new DojoStepperChip();
        plus.Configure("+");
        plus.Released += _ => state.Set(state.Value + 1);
        stepper.AddChild(plus);

        // Presets are a relic-row feature (the mock's card rows carry only the stepper).
        if (!model.IsCard && model.Spec.Primed is int primed)
        {
            var presets = new HBoxContainer();
            presets.AddThemeConstantOverride("separation", 8);
            presets.Alignment = BoxContainer.AlignmentMode.End;
            controls.AddChild(presets);

            presets.AddChild(MakePresetChip(state, "Zero", () => state.Set(0), activeWhen: value => value == 0));
            presets.AddChild(MakePresetChip(state, "Random",
                () => state.Set(Random.Shared.Next(model.Spec.Min, model.Spec.Max + 1)),
                activeWhen: null)); // Random is never shown "stuck" active
            presets.AddChild(MakePresetChip(state, "Primed", () => state.Set(primed),
                activeWhen: value => value == primed));
        }

        state.Set(model.Spec.Default);
        return rowPanel;
    }

    private static DojoPresetChip MakePresetChip(
        StateRow state, string text, Action apply, Func<int, bool>? activeWhen)
    {
        var chip = new DojoPresetChip();
        chip.Configure(text);
        chip.Released += _ => apply();
        state.Presets.Add((chip, activeWhen));
        return chip;
    }

    private static Control MakeStateIcon(Texture2D? texture, string displayName, bool circular)
    {
        var frame = new PanelContainer();
        frame.CustomMinimumSize = new Vector2(54, 54);
        frame.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        // Circle for relics (radius = half the 54px frame), rounded square for cards — matching the mock.
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(new Color("151A22"), StsColors.gold with { A = 0.8f }, circular ? 27 : 9);
        style.SetBorderWidthAll(2);
        style.SetContentMarginAll(7);
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
            // The mock's initials, kept as the fallback when the sprite can't be resolved.
            Label initials = DojoUi.MakeLabel(MakeInitials(displayName), 18, StsColors.gold);
            initials.HorizontalAlignment = HorizontalAlignment.Center;
            initials.VerticalAlignment = VerticalAlignment.Center;
            frame.AddChild(initials);
        }
        return frame;
    }

    private static string MakeInitials(string displayName)
    {
        string[] words = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return "?";
        }
        return words.Length == 1
            ? words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant()
            : string.Concat(words[0][0], words[1][0]).ToUpperInvariant();
    }

    // ------------------------------------------------------------------ actions

    private Control BuildActionBar(Vector2 buttonSize)
    {
        var bar = new HBoxContainer();
        bar.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        // The duplicates keep whatever size flags the popup scene baked in; pin them so the HBox lays
        // them out at their own size, left and right.
        _cancelButton!.CustomMinimumSize = buttonSize;
        _cancelButton.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _cancelButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        bar.AddChild(_cancelButton);

        var spacer = new Control();
        spacer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bar.AddChild(spacer);

        // §12a entry point 2: capture-and-export without playing. A preset-chip (not a third
        // NPopupYesNoButton) on purpose — IsYes wiring re-registers confirm/cancel hotkeys, and this
        // action must never steal ESC/confirm from Cancel/Start.
        _exportChip = new DojoPresetChip();
        _exportChip.Configure("Export Fight Code");
        _exportChip.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _exportChipLabel = _exportChip.GetChildren().OfType<Label>().FirstOrDefault();
        bar.AddChild(_exportChip);

        var spacer2 = new Control();
        spacer2.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bar.AddChild(spacer2);

        _startButton!.CustomMinimumSize = buttonSize;
        _startButton.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _startButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        bar.AddChild(_startButton);
        return bar;
    }

    /// <summary>Text/hotkeys/handlers. Called AFTER NModalContainer.Add: the buttons' _Ready (which
    /// resolves their %-name internals) has run by then, and IsYes re-registers hotkeys — green Start on
    /// confirm, red Cancel on ESC, same as the popup this modal replaces.</summary>
    private void WireActions()
    {
        if (_startButton == null || _cancelButton == null)
        {
            return;
        }

        _cancelButton.IsYes = false;
        _startButton.IsYes = true;
        _cancelButton.SetText("Cancel");
        _startButton.SetText("Start Replay");

        _cancelButton.Released += _ => NModalContainer.Instance?.Clear();
        _startButton.Released += _ =>
        {
            DojoStateAdjustments adjustments = BuildAdjustments();
            RunHistory history = _history;
            int floor = _globalFloor;
            NModalContainer.Instance?.Clear();
            TaskHelper.RunSafely(DojoReplayLauncher.LaunchReplay(history, floor, adjustments));
        };

        if (_exportChip != null)
        {
            _exportChip.Released += _ => TaskHelper.RunSafely(ExportWithoutPlaying());
        }
    }

    /// <summary>Runs the §12a prepare-only capture (identical sequence to Start, minus entering combat)
    /// with the CURRENT stepper/preset values baked in, then saves+copies via the shared exporter. The
    /// modal stays open — Start afterwards plays a fresh seed, which is expected: the export is its own
    /// fight, not a preview of the next launch.</summary>
    private async Task ExportWithoutPlaying()
    {
        if (_exportInFlight)
        {
            return;
        }
        _exportInFlight = true;
        SetExportChipText("Exporting…");
        try
        {
            DojoFightSnapshot? snapshot =
                await DojoReplayLauncher.PrepareReplaySnapshot(_history, _globalFloor, BuildAdjustments());
            SharedFightExporter.ExportResult result = SharedFightExporter.Export(snapshot);
            SetExportChipText(result.Message);
        }
        finally
        {
            _exportInFlight = false;
        }
    }

    private void SetExportChipText(string text)
    {
        if (_exportChip == null || _exportChipLabel == null)
        {
            return;
        }
        _exportChipLabel.Text = text;
        // Mirrors DojoPresetChip.Configure's sizing (13px font + 26px padding) — Configure itself can't
        // be re-run, it adds a fresh label each call.
        _exportChip.CustomMinimumSize = new Vector2(DojoUi.MeasureTextWidth(text, 13) + 26, 27f);
    }

    private DojoStateAdjustments BuildAdjustments()
    {
        var adjustments = new DojoStateAdjustments();
        foreach (StateRow row in _rows)
        {
            if (row.Value == row.Spec.Default)
            {
                continue; // the default IS what an unadjusted launch produces — nothing to stamp
            }
            if (row.IsCard)
            {
                adjustments.SetCard(row.Id, row.Occurrence, row.Spec.BuildProps(row.Value));
            }
            else
            {
                adjustments.SetRelic(row.Id, row.Spec.BuildProps(row.Value));
            }
        }
        return adjustments;
    }

    private static Label MakeOutlinedLabel(string text, int size, Color color)
    {
        Label label = DojoUi.MakeLabel(text, size, color);
        label.AddThemeColorOverride("font_outline_color", OutlineDark);
        label.AddThemeConstantOverride("outline_size", Mathf.Max(3, size / 5));
        return label;
    }

    /// <summary>One row's live value + the widgets that reflect it.</summary>
    private sealed class StateRow(DojoStateSpec spec, ModelId id, int occurrence, bool isCard)
    {
        public DojoStateSpec Spec { get; } = spec;
        public ModelId Id { get; } = id;
        public int Occurrence { get; } = occurrence;
        public bool IsCard { get; } = isCard;
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

/// <summary>A round − / + stepper button for the Replay Setup rows. Drawn flat like
/// <see cref="DojoChip"/> (StyleBoxFlat in _Draw, no scene assets).</summary>
public partial class DojoStepperChip : NButton
{
    private static readonly Color Bg = new("232B37");
    private static readonly Color BgHover = new("303B4A");
    private static readonly Color Border = new("3D4754");

    private const float ChipSize = 36f;

    private bool _hovered;

    protected override string[] Hotkeys => Array.Empty<string>();

    public void Configure(string glyph)
    {
        Label label = DojoUi.MakeLabel(glyph, 20, StsColors.cream);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(label);

        CustomMinimumSize = new Vector2(ChipSize, ChipSize);
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        ConnectSignals();
    }

    public override void _Draw()
    {
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(_hovered ? BgHover : Bg, _hovered ? StsColors.gold : Border, (int)(Size.Y / 2f));
        style.SetBorderWidthAll(1);
        style.Draw(GetCanvasItem(), new Rect2(Vector2.Zero, Size));
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        _hovered = true;
        QueueRedraw();
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        _hovered = false;
        QueueRedraw();
    }
}

/// <summary>A Zero/Random/Primed preset button: light parchment chip with dark text; gold fill while its
/// value is the row's current value (Random never shows active).</summary>
public partial class DojoPresetChip : NButton
{
    private static readonly Color Parchment = new("D8D0BC");
    private static readonly Color ParchmentHover = new("E7E0CC");
    private static readonly Color ActiveGold = new("E3AF4A");
    private static readonly Color ActiveGoldHover = new("EEBD5D");
    private static readonly Color Border = new("55503F");
    private static readonly Color ActiveBorder = new("8A6A20");
    private static readonly Color DarkText = new("2B2416");

    private const int FontSize = 13;
    private const float ChipHeight = 27f;

    private bool _selected;
    private bool _hovered;

    protected override string[] Hotkeys => Array.Empty<string>();

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            QueueRedraw();
        }
    }

    public void Configure(string text)
    {
        Label label = DojoUi.MakeLabel(text, FontSize, DarkText);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(label);

        CustomMinimumSize = new Vector2(DojoUi.MeasureTextWidth(text, FontSize) + 26, ChipHeight);
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        ConnectSignals();
    }

    public override void _Draw()
    {
        Color bg = _selected
            ? (_hovered ? ActiveGoldHover : ActiveGold)
            : (_hovered ? ParchmentHover : Parchment);
        StyleBoxFlat style = NDojoScreen.MakePanelStyle(bg, _selected ? ActiveBorder : Border, 6);
        style.SetBorderWidthAll(1);
        style.Draw(GetCanvasItem(), new Rect2(Vector2.Zero, Size));
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        _hovered = true;
        QueueRedraw();
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        _hovered = false;
        QueueRedraw();
    }
}
