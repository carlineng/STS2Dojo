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
            return false;
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
        return true;
    }

    private void WireButtons()
    {
        // NOT NPopupYesNoButton.SetText() — it resolves its target Label via a scene-unique name lookup
        // ("%Label") inside _Ready(), which doesn't reliably re-resolve for a node that was runtime-
        // duplicated rather than freshly instantiated from a scene (empirically confirmed in-game: all
        // three buttons rendered the template's baked-in "Yes" text, silently). Finding the label by
        // structural type within OUR OWN duplicated subtree sidesteps that entirely.
        SetButtonLabel(_tryAgainButton, "Try Again");
        SetButtonLabel(_returnToDojoButton, "Return to Dojo");
        SetButtonLabel(_returnToMainMenuButton, "Return to Main Menu");

        _tryAgainButton.Released += _ => TaskHelper.RunSafely(OnTryAgain());
        _returnToDojoButton.Released += _ => TaskHelper.RunSafely(OnReturnToDojo());
        _returnToMainMenuButton.Released += _ => TaskHelper.RunSafely(OnReturnToMainMenu());
    }

    private static void SetButtonLabel(Node root, string text)
    {
        if (root is Label label)
        {
            label.Text = text;
            return;
        }
        foreach (Node child in root.GetChildren())
        {
            SetButtonLabel(child, text);
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
