using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
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
/// <c>NPopupYesNoButton</c> node the base game's own generic confirm popup uses, for visual consistency
/// without authoring a new scene.
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
        screen.BuildLayout(won);

        // Add to the live tree BEFORE configuring the buttons below: NPopupYesNoButton.SetText touches a
        // private field only populated by the button's own _Ready()/ConnectSignals(), which Godot doesn't
        // run until the (sub)tree actually enters the scene tree — which NModalContainer.Add does
        // synchronously here, since NModalContainer is already inside the tree.
        modalContainer.Add(screen);
        screen.WireButtons();
    }

    private void BuildLayout(bool won)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var box = new VBoxContainer();
        center.AddChild(box);

        var header = new Label
        {
            Text = won ? "Victory!" : "Defeat",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        box.AddChild(header);

        NGenericPopup? buttonTemplateSource = NGenericPopup.Create();
        if (buttonTemplateSource == null)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not build Dojo completion screen buttons.");
            return;
        }

        NPopupYesNoButton buttonTemplate = buttonTemplateSource.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton");
        const int duplicateFlagsNoSignals = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts);
        _tryAgainButton = (NPopupYesNoButton)buttonTemplate.Duplicate(duplicateFlagsNoSignals);
        _returnToDojoButton = (NPopupYesNoButton)buttonTemplate.Duplicate(duplicateFlagsNoSignals);
        _returnToMainMenuButton = (NPopupYesNoButton)buttonTemplate.Duplicate(duplicateFlagsNoSignals);
        buttonTemplateSource.QueueFreeSafely();

        box.AddChild(_tryAgainButton);
        box.AddChild(_returnToDojoButton);
        box.AddChild(_returnToMainMenuButton);
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
