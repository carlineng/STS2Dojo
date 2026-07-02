using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Shown after a Dojo win/loss instead of the old direct-to-Run-History redirect (CLAUDE.md §9 roadmap item
/// 2b). Offers Try Again / Return to Dojo / Return to Main Menu. Shown as a modal directly on top of the
/// still-alive combat scene via <see cref="NModalContainer"/> — the same mechanism the game uses for
/// mid-run confirmations like "abandon run?" — so there's no need to return to the main menu just to show
/// it; that only happens once the player actually picks a destination.
///
/// This mod ships no <c>.pck</c>, so unlike every built-in screen (all <c>.tscn</c>-scene-based), this one
/// is a procedurally-built Godot node tree. Its three buttons are duplicates of the same themed
/// <c>NPopupYesNoButton</c> node the base game's own generic confirm popup uses, and its background/header
/// are extracted from the real rewards screen ("Rewards" stone panel + "%HeaderLabel"), for visual
/// consistency without authoring a new scene. The extraction is a best-effort, defensively wrapped attempt —
/// this mod has no access to the actual .tscn layout (only decompiled C#), so exact spacing/margins inside
/// the reused panel are approximated and may need visual tuning; if the extraction fails for any reason, this
/// falls back to the plain header+buttons layout that shipped before.
/// </summary>
public partial class DojoCompletionScreen : Control, IScreenContext
{
    private NPopupYesNoButton _tryAgainButton = null!;
    private NPopupYesNoButton _returnToDojoButton = null!;
    private NPopupYesNoButton _returnToMainMenuButton = null!;

    public Control? DefaultFocusedControl => _tryAgainButton;

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
        MouseFilter = MouseFilterEnum.Stop;

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 12);
        center.AddChild(stack);

        (Label header, Control buttonParent) = TryBuildRewardScreenChrome(won, stack)
            ?? BuildPlainChrome(won, stack);

        return BuildButtons(buttonParent);
    }

    /// <summary>Best-effort reuse of the real rewards screen's "Rewards" stone panel (a plain, unscripted
    /// Control — safe to reparent) and "%HeaderLabel" (the "Loot!"-style title font) by instantiating the
    /// rewards screen scene OFF-TREE and extracting both before its own _Ready()/overlay lifecycle ever runs.
    /// Returns null (never partially applies anything to <paramref name="stack"/>) if any step fails, so the
    /// caller can cleanly fall back to <see cref="BuildPlainChrome"/> instead.</summary>
    private static (Label Header, Control ButtonParent)? TryBuildRewardScreenChrome(bool won, VBoxContainer stack)
    {
        Control? panel = null;
        MegaLabel? headerLabel = null;
        try
        {
            string scenePath = SceneHelper.GetScenePath("screens/rewards_screen");
            var templateScreen = PreloadManager.Cache.GetScene(scenePath).Instantiate<NRewardsScreen>();

            panel = templateScreen.GetNode<Control>("Rewards");
            headerLabel = templateScreen.GetNode<MegaLabel>("%HeaderLabel");

            panel.GetParent().RemoveChild(panel);
            headerLabel.GetParent().RemoveChild(headerLabel);
            templateScreen.QueueFreeSafely();

            // Must happen before these enter the tree — see DojoNodeDuplication's class docs. "Rewards" is a
            // plain Control (no script of its own) so this is likely a no-op for it, but headerLabel's own
            // rich-text styling may depend on unique names scoped within its own subtree.
            DojoNodeDuplication.ReownRecursively(panel);
            DojoNodeDuplication.ReownRecursively(headerLabel);

            // _Ready() (which we're deliberately not running — it belongs to NRewardsScreen's overlay
            // lifecycle, which this screen has nothing to do with) is what normally makes the panel visible;
            // without it the panel may retain a transparent design-time default. Force it visible.
            panel.Modulate = Colors.White;
            panel.CustomMinimumSize = new Vector2(560, 520);

            headerLabel.HorizontalAlignment = HorizontalAlignment.Center;
            headerLabel.SetTextAutoSize(won ? "Victory!" : "Defeat");

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 60);
            margin.AddThemeConstantOverride("margin_right", 60);
            margin.AddThemeConstantOverride("margin_top", 70);
            margin.SetAnchorsPreset(LayoutPreset.TopWide);
            panel.AddChild(margin);

            // Nothing is added to `stack` until everything above has succeeded without throwing — if any of
            // it fails partway, `stack` must be left untouched so the caller's fallback to BuildPlainChrome
            // doesn't end up with a stray half-built header/panel alongside the plain one.
            stack.AddChild(headerLabel);
            stack.AddChild(panel);

            return (headerLabel, margin);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not reuse rewards-screen chrome for completion screen, " +
                "falling back to a plain layout: " + e);
            // Neither node made it into `stack` (or anywhere else) if this was reached — free them explicitly
            // so a partial failure doesn't leak two detached nodes.
            panel?.QueueFreeSafely();
            headerLabel?.QueueFreeSafely();
            return null;
        }
    }

    /// <summary>The plain layout this screen shipped with before attempting reward-screen chrome reuse —
    /// used verbatim as the fallback if that reuse fails for any reason.</summary>
    private static (Label Header, Control ButtonParent) BuildPlainChrome(bool won, VBoxContainer stack)
    {
        var header = new Label
        {
            Text = won ? "Victory!" : "Defeat",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stack.AddChild(header);
        return (header, stack);
    }

    private bool BuildButtons(Control buttonParent)
    {
        var box = new VBoxContainer();
        buttonParent.AddChild(box);

        NGenericPopup? buttonTemplateSource = NGenericPopup.Create();
        if (buttonTemplateSource == null)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not build Dojo completion screen buttons.");
            return false;
        }

        NPopupYesNoButton buttonTemplate = buttonTemplateSource.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton");
        const int duplicateFlagsNoSignals = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts);
        _tryAgainButton = (NPopupYesNoButton)buttonTemplate.Duplicate(duplicateFlagsNoSignals);
        _returnToDojoButton = (NPopupYesNoButton)buttonTemplate.Duplicate(duplicateFlagsNoSignals);
        _returnToMainMenuButton = (NPopupYesNoButton)buttonTemplate.Duplicate(duplicateFlagsNoSignals);
        buttonTemplateSource.QueueFreeSafely();

        // Must happen before each duplicate enters the tree — see DojoNodeDuplication's class docs. Without
        // this, NPopupYesNoButton._Ready()'s "%Visuals"/"%Label"/etc lookups fail (the button was designed
        // for short "Yes"/"No" text and a fixed width, so it also needs widening for our longer labels).
        const float buttonWidth = 420f;
        foreach (NPopupYesNoButton button in new[] { _tryAgainButton, _returnToDojoButton, _returnToMainMenuButton })
        {
            DojoNodeDuplication.ReownRecursively(button);
            button.CustomMinimumSize = new Vector2(buttonWidth, button.CustomMinimumSize.Y);
        }

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

        // The controller-hotkey icon (the "A" glyph) doesn't make sense here — Yes/No's "confirm"/"cancel"
        // semantics don't map onto three peer options — so hide it outright rather than leaving it to
        // NButton's own "visible only if a controller is in use" logic.
        foreach (NPopupYesNoButton button in new[] { _tryAgainButton, _returnToDojoButton, _returnToMainMenuButton })
        {
            button.GetNodeOrNull<TextureRect>("%ControllerIcon")?.Hide();
        }

        _tryAgainButton.Released += _ => TaskHelper.RunSafely(OnTryAgain());
        _returnToDojoButton.Released += _ => TaskHelper.RunSafely(OnReturnToDojo());
        _returnToMainMenuButton.Released += _ => TaskHelper.RunSafely(OnReturnToMainMenu());
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
        DojoRunBrowser.Open(game);
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
