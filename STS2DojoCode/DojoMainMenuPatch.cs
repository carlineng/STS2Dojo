using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Injects a "Dojo" button into the modded main menu (CLAUDE.md §9 roadmap item 5). <c>NMainMenu</c> has no
/// runtime-appendable button list — <c>MainMenuButtons</c> is a computed array over 8 hardcoded scene-baked
/// fields — so the button is a duplicate of an existing themed <see cref="NMainMenuTextButton"/> node,
/// reparented, relabeled, and rewired, rather than a newly authored scene (this mod ships no <c>.pck</c>).
/// </summary>
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
public static class DojoMainMenuPatch
{
    // NMainMenu.MainMenuButtonFocused/Unfocused (the gold hover-reticle animation) are private — reflection
    // is the only way to hook our duplicated button into that same animation, since
    // NMainMenu.ConnectMainMenuTextButtonFocusLogic() already ran (over the buttons that existed before
    // this postfix) by the time this runs.
    private static readonly MethodInfo FocusedMethod = AccessTools.Method(typeof(NMainMenu), "MainMenuButtonFocused");
    private static readonly MethodInfo UnfocusedMethod = AccessTools.Method(typeof(NMainMenu), "MainMenuButtonUnfocused");

    // Groups + Scripts only — deliberately NOT Node.DuplicateFlags.Signals. NMainMenu._Ready() has already
    // connected the template button's own Released signal (e.g. the settings button's, to OpenSettingsMenu)
    // by the time this postfix runs; including Signals would carry that live connection over too.
    private const int DuplicateFlagsNoSignals = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts);

    // ReSharper disable once UnusedMember.Global
    public static void Postfix(NMainMenu __instance)
    {
        Control container = __instance.GetNode<Control>("MainMenuTextButtons");
        NMainMenuTextButton template = __instance.GetNode<NMainMenuTextButton>("MainMenuTextButtons/SettingsButton");

        var dojoButton = (NMainMenuTextButton)template.Duplicate(DuplicateFlagsNoSignals);

        // Must happen before the duplicate enters the tree — see DojoNodeDuplication's class docs. NButton's
        // ConnectSignals() (which NMainMenuTextButton inherits into) resolves a "%ControllerIcon" unique
        // name; without this, that lookup silently fails and the button can be left showing a stray
        // controller-hotkey icon.
        DojoNodeDuplication.ReownRecursively(dojoButton);

        // Set the label directly via the node tree (bypassing SetLocalization/LocString, which this mod has
        // no table entries for) — safe to do immediately, since Duplicate() copies the structural child tree
        // regardless of whether _Ready() has run on the duplicate yet.
        MegaLabel? dojoLabel = dojoButton.GetChild<MegaLabel>(0);
        if (dojoLabel != null)
        {
            dojoLabel.Text = "Dojo";
        }

        container.AddChildSafely(dojoButton);

        // AddChildSafely appends at the end (after Quit); move it to sit right before Settings (i.e.
        // between Multiplayer/Timeline and Settings) instead. template IS the SettingsButton node, so its
        // current index is exactly the target slot.
        container.MoveChild(dojoButton, template.GetIndex());

        dojoButton.Visible = true;
        dojoButton.Enable();

        dojoButton.Released += _ =>
        {
            NGame? game = NGame.Instance;
            if (game != null)
            {
                NDojoScreen.Open(game);
            }
        };
        dojoButton.Focused += b => FocusedMethod.Invoke(__instance, new object[] { b });
        dojoButton.Unfocused += b => UnfocusedMethod.Invoke(__instance, new object[] { b });
    }
}
