using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Shown after a Dojo win/loss instead of the old direct-to-Run-History redirect (CLAUDE.md §9 roadmap item
/// 2b). Offers Try Again / Return to Dojo / Return to Main Menu. Shown as a modal directly on top of the
/// still-alive combat scene via <see cref="NModalContainer"/> — the same mechanism the game uses for
/// mid-run confirmations like "abandon run?" — so there's no need to return to the main menu just to show
/// it; that only happens once the player actually picks a destination.
///
/// This mod ships no <c>.pck</c>, so unlike every built-in screen (all <c>.tscn</c>-scene-based), this one
/// is a procedurally-built Godot node tree. Its three buttons use an event-option-style drawn button with
/// the base game's popup button label style. Its centered bounds and title style are borrowed from the real
/// rewards screen ("Rewards" + "%HeaderLabel") for visual consistency without authoring a new scene. The
/// extraction is a best-effort, defensively wrapped attempt —
/// this mod has no access to the actual .tscn layout (only decompiled C#), so exact spacing/margins inside
/// the reused panel are approximated and may need visual tuning; if the extraction fails for any reason, this
/// falls back to the plain header+buttons layout that shipped before.
///
/// This class is a §5m (CLAUDE.md) landmine: its script-dispatch bridge is broken in the modded game —
/// every engine call into it throws (its _Ready/_ExitTree overrides never actually run), and the engine
/// renders the swallowed exception as a literal "&lt;null&gt;" native tooltip on hover. The exact
/// discriminator is unknown (game-class base and lifecycle-override theories were both falsified in-game;
/// see §5m). It cannot dodge the problem the way <see cref="DojoRunRow"/> does (a plain non-node class
/// owning script-less nodes) because <c>NModalContainer.Add</c> hard-casts the node it is given to
/// <see cref="IScreenContext"/>, so the node itself must implement the interface. The load-bearing defense
/// is the BuildLayout mouse-filter structure that keeps this node out of every native tooltip walk —
/// verified in-game 2026-07-03. The <see cref="NTopBarPortrait"/> base (inert; its only member,
/// <c>Initialize(Player)</c>, is never called) is kept but known not to matter. The residual §5m exception
/// spam from lifecycle/layout dispatch into this class (~60 log lines per completion-screen visit) is
/// cosmetic.
/// </summary>
public partial class DojoCompletionScreen : NTopBarPortrait, IScreenContext
{
    private const float PanelWidth = 720f;
    private const float PanelHeight = 540f;
    private const float ButtonWidth = 620f;
    private const float ButtonHeight = 82f;
    private const float ButtonTextHorizontalPadding = 52f;
    private const float BannerHeight = 78f;
    private const int DuplicateFlagsNoSignals = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts);

    private static readonly FieldInfo? ActiveHoverTipsField =
        typeof(NHoverTipSet).GetField("_activeHoverTips", BindingFlags.NonPublic | BindingFlags.Static);

    private DojoCompletionEventOptionButton _tryAgainButton = null!;
    private DojoCompletionEventOptionButton _returnToDojoButton = null!;
    private DojoCompletionEventOptionButton _returnToMainMenuButton = null!;
    private CanvasItem? _hoverTipsContainerCanvasItem;
    private bool _blockedHoverTips;
    private bool _previousHoverTipsContainerVisible;
    private bool _previousShouldBlockHoverTips;

    public Control? DefaultFocusedControl => _tryAgainButton;

    public override void _ExitTree()
    {
        RestoreHoverTips();
    }

    public static void Show(bool won)
    {
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer == null)
        {
            MainFile.Logger.Error("[STS2Dojo] Cannot show Dojo completion screen: no NModalContainer.");
            return;
        }

        var screen = new DojoCompletionScreen();
        if (!screen.BuildLayout(won))
        {
            // BuildLayout already logged why. screen was never added to any tree, so it must be freed
            // explicitly here or it leaks (Godot Nodes aren't reference-counted/GC-collected).
            screen.QueueFreeSafely();
            return;
        }

        modalContainer.Add(screen);
        screen.WireButtons();
    }

    /// <returns>False if the buttons couldn't be built (already logged) — the screen is left partially
    /// constructed and unusable; the caller must not add it to the tree or call <see cref="WireButtons"/>.</returns>
    private bool BuildLayout(bool won)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        // This node must be unreachable by the native tooltip walk: if this class's script dispatch is
        // broken (§5m), Viewport._gui_get_tooltip turns the swallowed exception into a literal "<null>"
        // tooltip. The walk starts at the hovered control and ascends parents — calling get_tooltip on
        // every node INCLUDING the MOUSE_FILTER_STOP one that terminates it — so this root is Ignore
        // (never hovered, never ascended into) and the full-rect center container below is the script-less
        // Stop control that both swallows modal input (the job this root's Stop filter used to do) and
        // ends every tooltip walk before it can reach this node.
        MouseFilter = MouseFilterEnum.Ignore;
        BlockHoverTips();

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Stop;
        AddChild(center);

        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 0);
        center.AddChild(stack);

        Control buttonParent = TryBuildRewardScreenChrome(won, stack)
            ?? BuildPlainChrome(won, stack);

        bool built = BuildButtons(buttonParent);
        if (!built)
        {
            RestoreHoverTips();
        }

        return built;
    }

    /// <summary>Best-effort reuse of the real rewards screen's "Rewards" stone panel (a plain, unscripted
    /// Control — safe to reparent) by instantiating the rewards screen scene OFF-TREE and extracting that
    /// one frame before its own _Ready()/overlay lifecycle ever runs. The borrowed title node is NOT reused:
    /// it carries scene-specific offsets/state that produced visual drift and stray text in this screen, so
    /// the Dojo completion screen renders its own title label on top of the shared frame instead. Returns
    /// null (never partially applies anything to <paramref name="stack"/>) if any step fails, so the caller
    /// can cleanly fall back to <see cref="BuildPlainChrome"/> instead.</summary>
    private static Control? TryBuildRewardScreenChrome(bool won, VBoxContainer stack)
    {
        Control? panel = null;
        try
        {
            string scenePath = SceneHelper.GetScenePath("screens/rewards_screen");
            var templateScreen = PreloadManager.Cache.GetScene(scenePath).Instantiate<NRewardsScreen>();

            panel = templateScreen.GetNode<Control>("Rewards");
            MegaLabel headerTemplate = templateScreen.GetNode<MegaLabel>("%HeaderLabel");
            MegaLabel headerLabel = CreateHeaderLabel(won, headerTemplate);

            panel.GetParent().RemoveChild(panel);
            templateScreen.QueueFreeSafely();

            // The reward screen carries a lot of child nodes we don't want (scrollbars, containers, tooltip
            // sources, etc). Keep its centered bounds as an empty shell, then rebuild the completion-screen
            // contents inside that clean frame.
            RemoveAllChildren(panel);

            DojoNodeDuplication.ReownRecursively(panel);

            // _Ready() (which we're deliberately not running — it belongs to NRewardsScreen's overlay
            // lifecycle, which this screen has nothing to do with) is what normally makes the panel visible;
            // without it the panel may retain a transparent design-time default. Force it visible.
            panel.Modulate = Colors.White;
            panel.CustomMinimumSize = new Vector2(PanelWidth, PanelHeight);
            SetTopLeftRect(panel, PanelWidth, PanelHeight);
            panel.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            panel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            panel.PivotOffset = new Vector2(PanelWidth / 2f, PanelHeight / 2f);
            panel.AddChild(headerLabel);

            var layout = new MarginContainer();
            layout.AddThemeConstantOverride("margin_left", 42);
            layout.AddThemeConstantOverride("margin_right", 42);
            layout.AddThemeConstantOverride("margin_top", 118);
            layout.AddThemeConstantOverride("margin_bottom", 38);
            layout.SetAnchorsPreset(LayoutPreset.FullRect);
            panel.AddChild(layout);

            var buttonCenter = new CenterContainer();
            buttonCenter.SetAnchorsPreset(LayoutPreset.FullRect);
            buttonCenter.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            buttonCenter.SizeFlagsVertical = SizeFlags.ExpandFill;
            layout.AddChild(buttonCenter);

            // Nothing is added to `stack` until everything above has succeeded without throwing — if any of
            // it fails partway, `stack` must be left untouched so the caller's fallback to BuildPlainChrome
            // doesn't end up with a stray half-built header/panel alongside the plain one.
            stack.AddChild(panel);

            return buttonCenter;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not reuse rewards-screen chrome for completion screen, " +
                "falling back to a plain layout: " + e);
            // Neither node made it into `stack` (or anywhere else) if this was reached — free them explicitly
            // so a partial failure doesn't leak two detached nodes.
            panel?.QueueFreeSafely();
            return null;
        }
    }

    /// <summary>The plain layout this screen shipped with before attempting reward-screen chrome reuse —
    /// used verbatim as the fallback if that reuse fails for any reason.</summary>
    private static Control BuildPlainChrome(bool won, VBoxContainer stack)
    {
        var chrome = new VBoxContainer();
        chrome.Alignment = BoxContainer.AlignmentMode.Center;
        chrome.AddThemeConstantOverride("separation", 24);
        chrome.CustomMinimumSize = new Vector2(PanelWidth, 0);
        stack.AddChild(chrome);

        chrome.AddChild(CreateHeaderLabel(won));

        var buttonCenter = new CenterContainer();
        buttonCenter.CustomMinimumSize = new Vector2(PanelWidth, 0);
        chrome.AddChild(buttonCenter);
        return buttonCenter;
    }

    private bool BuildButtons(Control buttonParent)
    {
        var box = new VBoxContainer();
        box.Alignment = BoxContainer.AlignmentMode.Center;
        box.AddThemeConstantOverride("separation", 14);
        box.CustomMinimumSize = new Vector2(ButtonWidth, 0);
        box.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        buttonParent.AddChild(box);

        MegaLabel? labelTemplate = TryCreateButtonLabelTemplate();
        if (labelTemplate == null)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not build Dojo completion screen buttons.");
            return false;
        }

        _tryAgainButton = CreateEventOptionButton(labelTemplate, "Try Again");
        _returnToDojoButton = CreateEventOptionButton(labelTemplate, "Return to Dojo");
        _returnToMainMenuButton = CreateEventOptionButton(labelTemplate, "Return to Main Menu");
        labelTemplate.QueueFreeSafely();

        box.AddChild(_tryAgainButton);
        box.AddChild(_returnToDojoButton);
        box.AddChild(_returnToMainMenuButton);
        return true;
    }

    private void WireButtons()
    {
        _tryAgainButton.SetText("Try Again");
        _returnToDojoButton.SetText("Return to Dojo");
        _returnToMainMenuButton.SetText("Return to Main Menu");

        _tryAgainButton.Released += _ => TaskHelper.RunSafely(OnTryAgain());
        _returnToDojoButton.Released += _ => TaskHelper.RunSafely(OnReturnToDojo());
        _returnToMainMenuButton.Released += _ => TaskHelper.RunSafely(OnReturnToMainMenu());
    }

    private static MegaLabel? TryCreateButtonLabelTemplate()
    {
        try
        {
            NGenericPopup? popup = NGenericPopup.Create();
            if (popup == null)
            {
                return null;
            }

            MegaLabel template = popup.GetNode<MegaLabel>("VerticalPopup/YesButton/%Label");
            var label = (MegaLabel)template.Duplicate(DuplicateFlagsNoSignals);
            DojoNodeDuplication.ReownRecursively(label);
            popup.QueueFreeSafely();
            return label;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not instantiate button label template: " + e);
            return null;
        }
    }

    private static DojoCompletionEventOptionButton CreateEventOptionButton(MegaLabel labelTemplate, string text)
    {
        var button = new DojoCompletionEventOptionButton();
        button.Name = text.Replace(" ", string.Empty) + "Button";

        var label = (MegaLabel)labelTemplate.Duplicate(DuplicateFlagsNoSignals);
        label.Name = "Label";
        button.AddChild(label);
        DojoNodeDuplication.ReownRecursively(button);

        ConfigureButtonChrome(button);
        button.SetText(text);
        return button;
    }

    private static void ConfigureButtonChrome(DojoCompletionEventOptionButton button)
    {
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        button.CustomMinimumSize = new Vector2(ButtonWidth, ButtonHeight);
        button.FocusMode = FocusModeEnum.All;
        button.MouseFilter = MouseFilterEnum.Stop;
        button.Position = Vector2.Zero;
        button.PivotOffset = new Vector2(ButtonWidth / 2f, ButtonHeight / 2f);

        MegaLabel? label = button.GetNodeOrNull<MegaLabel>("Label");
        if (label == null)
        {
            return;
        }

        label.Visible = true;
        SetFullRect(label);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.OffsetLeft = ButtonTextHorizontalPadding;
        label.OffsetTop = -3f;
        label.OffsetRight = -ButtonTextHorizontalPadding;
        label.OffsetBottom = -3f;
        label.MouseFilter = MouseFilterEnum.Ignore;
    }

    private static void RemoveAllChildren(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFreeSafely();
        }
    }

    private static MegaLabel CreateHeaderLabel(bool won, MegaLabel? template = null)
    {
        var headerLabel = template != null
            ? (MegaLabel)template.Duplicate(DuplicateFlagsNoSignals)
            : new MegaLabel();

        DojoNodeDuplication.ReownRecursively(headerLabel);

        headerLabel.Name = "HeaderLabel";
        headerLabel.Visible = true;
        headerLabel.Modulate = Colors.White;
        headerLabel.HorizontalAlignment = HorizontalAlignment.Center;
        headerLabel.VerticalAlignment = VerticalAlignment.Center;
        headerLabel.MouseFilter = MouseFilterEnum.Ignore;
        headerLabel.CustomMinimumSize = new Vector2(PanelWidth, BannerHeight);
        if (template == null)
        {
            headerLabel.MinFontSize = 32;
            headerLabel.MaxFontSize = 44;
        }
        SetFullRect(headerLabel);
        headerLabel.AnchorBottom = 0f;
        headerLabel.OffsetBottom = BannerHeight;
        headerLabel.SetTextAutoSize(won ? "Victory!" : "Defeat");
        return headerLabel;
    }

    private static void SetFullRect(Control control)
    {
        control.AnchorLeft = 0f;
        control.AnchorTop = 0f;
        control.AnchorRight = 1f;
        control.AnchorBottom = 1f;
        control.OffsetLeft = 0f;
        control.OffsetTop = 0f;
        control.OffsetRight = 0f;
        control.OffsetBottom = 0f;
    }

    private static void SetTopLeftRect(Control control, float width, float height)
    {
        control.AnchorLeft = 0f;
        control.AnchorTop = 0f;
        control.AnchorRight = 0f;
        control.AnchorBottom = 0f;
        control.OffsetLeft = 0f;
        control.OffsetTop = 0f;
        control.OffsetRight = width;
        control.OffsetBottom = height;
    }

    private void BlockHoverTips()
    {
        if (_blockedHoverTips)
        {
            return;
        }

        _previousShouldBlockHoverTips = NHoverTipSet.shouldBlockHoverTips;
        NHoverTipSet.shouldBlockHoverTips = true;

        _hoverTipsContainerCanvasItem = NGame.Instance?.HoverTipsContainer as CanvasItem;
        if (_hoverTipsContainerCanvasItem != null)
        {
            _previousHoverTipsContainerVisible = _hoverTipsContainerCanvasItem.Visible;
            _hoverTipsContainerCanvasItem.Hide();
        }

        _blockedHoverTips = true;
        ClearGameHoverTips();
    }

    private void RestoreHoverTips()
    {
        if (!_blockedHoverTips)
        {
            return;
        }

        NHoverTipSet.shouldBlockHoverTips = _previousShouldBlockHoverTips;
        if (_hoverTipsContainerCanvasItem != null)
        {
            _hoverTipsContainerCanvasItem.Visible = _previousHoverTipsContainerVisible;
            _hoverTipsContainerCanvasItem = null;
        }

        _blockedHoverTips = false;
    }

    private static void ClearGameHoverTips()
    {
        if (ActiveHoverTipsField?.GetValue(null) is IDictionary activeHoverTips)
        {
            var hoverTips = new List<NHoverTipSet>();
            foreach (object? value in activeHoverTips.Values)
            {
                if (value is NHoverTipSet hoverTip)
                {
                    hoverTips.Add(hoverTip);
                }
            }

            foreach (NHoverTipSet hoverTip in hoverTips)
            {
                hoverTip.QueueFreeSafely();
            }
            activeHoverTips.Clear();
            return;
        }

        Node? hoverTipsContainer = NGame.Instance?.HoverTipsContainer;
        if (hoverTipsContainer == null)
        {
            return;
        }

        foreach (Node child in hoverTipsContainer.GetChildren())
        {
            child.QueueFreeSafely();
        }
    }

    private static async Task OnTryAgain()
    {
        NModalContainer.Instance?.Clear();
        await DojoLaunch.TryAgain();
    }

    private static async Task OnReturnToDojo()
    {
        NModalContainer.Instance?.Clear();
        NGame? game = NGame.Instance;
        if (game == null)
        {
            return;
        }
        await game.ReturnToMainMenu();
        NDojoScreen.Open(game);
    }

    private static async Task OnReturnToMainMenu()
    {
        NModalContainer.Instance?.Clear();
        NGame? game = NGame.Instance;
        if (game != null)
        {
            await game.ReturnToMainMenu();
        }
    }
}

public partial class DojoCompletionEventOptionButton : NButton
{
    private static readonly Color FillTop = new("725F44");
    private static readonly Color FillMiddle = new("554632");
    private static readonly Color FillBottom = new("3F3426");
    private static readonly Color FillFocusedTop = new("9B7E4E");
    private static readonly Color FillFocusedMiddle = new("735537");
    private static readonly Color FillFocusedBottom = new("5B4328");
    private static readonly Color FillPressedTop = new("574229");
    private static readonly Color FillPressedMiddle = new("42301F");
    private static readonly Color FillPressedBottom = new("2C2116");
    private static readonly Color Border = new("201B15");
    private static readonly Color BorderFocused = new("F0B400");
    private static readonly Color Shadow = new("050403B0");
    private static readonly Color TopHighlight = new("C6A05A44");

    private MegaLabel _label = null!;
    private Tween? _tween;
    private string _text = string.Empty;
    private bool _focused;
    private bool _pressed;

    protected override string[] Hotkeys => Array.Empty<string>();

    public override void _Ready()
    {
        ConnectSignals();
        _label = GetNode<MegaLabel>("Label");
        ApplyText();
        ResetVisualState();
    }

    public override void _Draw()
    {
        Vector2 size = Size;
        Vector2[] shadow = BuildShape(size, 10f, 5f);
        DrawPolygon(shadow, new[] { Shadow });

        Vector2[] body = BuildShape(size, 8f, 0f);
        (Color top, Color middle, Color bottom) = GetFillColors();
        DrawPolygon(body, new[] { middle });
        DrawPolygon(BuildBand(size, 8f, size.Y * 0.43f, 8f, 0f), new[] { top });
        DrawPolygon(BuildBand(size, size.Y * 0.58f, size.Y - 8f, 8f, 0f), new[] { bottom });

        Vector2[] border = CloseShape(body);
        DrawPolyline(border, _focused ? BorderFocused : Border, _focused ? 5f : 4f, true);
        float notch = GetNotch(size);
        DrawPolyline(
            new[] { new Vector2(notch + 12f, 15f), new Vector2(size.X - notch - 12f, 15f) },
            TopHighlight,
            2f,
            true);

        if (_focused)
        {
            Vector2[] glow = BuildShape(size, 5f, 0f);
            DrawPolyline(CloseShape(glow), new Color("F0B40066"), 10f, true);
        }
    }

    public void SetText(string text)
    {
        _text = text;
        if (_label != null)
        {
            ApplyText();
        }
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        _focused = true;
        _pressed = false;
        KillTween();
        _tween = CreateTween().SetParallel(true);
        _tween.TweenProperty(this, "scale", Vector2.One * 1.035f, 0.05);
        QueueRedraw();
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        _focused = false;
        _pressed = false;
        KillTween();
        _tween = CreateTween().SetParallel(true);
        _tween.TweenProperty(this, "scale", Vector2.One, 0.35).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        QueueRedraw();
    }

    protected override void OnPress()
    {
        base.OnPress();
        _pressed = true;
        KillTween();
        _tween = CreateTween().SetParallel(true);
        _tween.TweenProperty(this, "scale", Vector2.One * 0.96f, 0.12).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        QueueRedraw();
    }

    protected override void OnRelease()
    {
        _pressed = false;
        KillTween();
        _tween = CreateTween().SetParallel(true);
        _tween.TweenProperty(this, "scale", _focused ? Vector2.One * 1.035f : Vector2.One, 0.05);
        QueueRedraw();
    }

    private void ApplyText() => _label.SetTextAutoSize(_text);

    private void ResetVisualState()
    {
        Scale = Vector2.One;
        _focused = false;
        _pressed = false;
        QueueRedraw();
    }

    private void KillTween()
    {
        if (_tween != null)
        {
            _tween.Kill();
            _tween = null;
        }
    }

    private static Vector2[] BuildShape(Vector2 size, float verticalInset, float yOffset)
    {
        float notch = GetNotch(size);
        float top = verticalInset + yOffset;
        float bottom = size.Y - verticalInset + yOffset;
        float middle = size.Y / 2f + yOffset;
        return new[]
        {
            new Vector2(notch, top),
            new Vector2(size.X - notch, top),
            new Vector2(size.X, middle),
            new Vector2(size.X - notch, bottom),
            new Vector2(notch, bottom),
            new Vector2(0f, middle),
        };
    }

    private (Color Top, Color Middle, Color Bottom) GetFillColors()
    {
        if (_pressed)
        {
            return (FillPressedTop, FillPressedMiddle, FillPressedBottom);
        }

        if (_focused)
        {
            return (FillFocusedTop, FillFocusedMiddle, FillFocusedBottom);
        }

        return (FillTop, FillMiddle, FillBottom);
    }

    private static Vector2[] BuildBand(Vector2 size, float yTop, float yBottom, float verticalInset, float yOffset)
    {
        float shapeTop = verticalInset + yOffset;
        float shapeBottom = size.Y - verticalInset + yOffset;
        yTop = Clamp(yTop, shapeTop, shapeBottom);
        yBottom = Clamp(yBottom, shapeTop, shapeBottom);

        return new[]
        {
            new Vector2(LeftEdgeXAtY(size, verticalInset, yOffset, yTop), yTop),
            new Vector2(RightEdgeXAtY(size, verticalInset, yOffset, yTop), yTop),
            new Vector2(RightEdgeXAtY(size, verticalInset, yOffset, yBottom), yBottom),
            new Vector2(LeftEdgeXAtY(size, verticalInset, yOffset, yBottom), yBottom),
        };
    }

    private static float LeftEdgeXAtY(Vector2 size, float verticalInset, float yOffset, float y)
    {
        float notch = GetNotch(size);
        float top = verticalInset + yOffset;
        float bottom = size.Y - verticalInset + yOffset;
        float middle = size.Y / 2f + yOffset;
        if (y <= middle)
        {
            return Lerp(notch, 0f, Ratio(y, top, middle));
        }
        return Lerp(0f, notch, Ratio(y, middle, bottom));
    }

    private static float RightEdgeXAtY(Vector2 size, float verticalInset, float yOffset, float y)
    {
        float notch = GetNotch(size);
        float top = verticalInset + yOffset;
        float bottom = size.Y - verticalInset + yOffset;
        float middle = size.Y / 2f + yOffset;
        if (y <= middle)
        {
            return Lerp(size.X - notch, size.X, Ratio(y, top, middle));
        }
        return Lerp(size.X, size.X - notch, Ratio(y, middle, bottom));
    }

    private static float GetNotch(Vector2 size) => MathF.Min(38f, size.X * 0.085f);

    private static float Ratio(float value, float min, float max)
    {
        if (Math.Abs(max - min) < 0.001f)
        {
            return 0f;
        }
        return Clamp((value - min) / (max - min), 0f, 1f);
    }

    private static float Lerp(float from, float to, float weight) => from + (to - from) * weight;

    private static float Clamp(float value, float min, float max) => Math.Min(Math.Max(value, min), max);

    private static Vector2[] CloseShape(Vector2[] shape)
    {
        var closed = new Vector2[shape.Length + 1];
        Array.Copy(shape, closed, shape.Length);
        closed[^1] = shape[0];
        return closed;
    }
}
