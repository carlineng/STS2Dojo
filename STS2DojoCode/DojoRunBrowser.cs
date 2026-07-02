using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Opens the real (non-modded) profile's Run History as the Dojo's run browser, by reusing the game's own
/// <see cref="NRunHistory"/> screen (CLAUDE.md §9 roadmap item 5). <c>NRunHistory</c>/<c>SaveManager</c>
/// resolve their history directory from the LIVE value of <c>UserDataPathProvider.IsRunningModded</c> on
/// every call — re-read on every button press, not cached — so flipping it to <c>false</c> for the duration
/// the screen is open redirects it to the real profile's <c>.run</c> files with no patch needed on
/// <c>SaveManager</c>/<c>NRunHistory</c> itself. The flag is restored via two independent hooks, because
/// there are two independent ways the screen stops being open:
/// <list type="bullet">
/// <item>Backing out of the screen pops it off the submenu stack, firing <c>NSubmenu.OnSubmenuClosed</c> —
/// see <see cref="DojoRunHistoryFlagRestorePatch"/>.</item>
/// <item>Confirming a floor launches straight into the fight, which replaces the main menu scene entirely
/// via <c>NSceneContainer.SetCurrentScene</c> (it frees the old scene's children directly — see
/// <c>NSceneContainer.SetCurrentScene</c> in the decompiled source) WITHOUT ever popping the submenu stack
/// first, so <c>OnSubmenuClosed</c> never fires on this path. Without the second hook below, every
/// successful replay launch would permanently leave the whole modded session reading/writing the real
/// profile. See <see cref="DojoRunHistorySceneSwapRestorePatch"/>.</item>
/// </list>
/// Both hooks call the same <see cref="ConsumeRestorePending"/>, so whichever fires first wins and the
/// other is a no-op.
/// </summary>
public static class DojoRunBrowser
{
    private static bool _restorePending;

    public static void Open(NGame game)
    {
        NMainMenu? mainMenu = game.MainMenu;
        if (mainMenu == null)
        {
            MainFile.Logger.Error("[STS2Dojo] Cannot open Dojo run browser: not currently on the main menu.");
            return;
        }

        UserDataPathProvider.IsRunningModded = false;

        // NRunHistory.OnSubmenuShown throws if this is false (a real Steam profile with literally zero
        // prior runs) — check up front so a first-time player gets a log line instead of an exception.
        if (SaveManager.Instance.GetRunHistoryCount() <= 0)
        {
            MainFile.Logger.Info("[STS2Dojo] Real profile has no run history yet — nothing to show in the Dojo.");
            UserDataPathProvider.IsRunningModded = true;
            return;
        }

        _restorePending = true;
        try
        {
            mainMenu.SubmenuStack.PushSubmenuType<NRunHistory>();
        }
        catch
        {
            _restorePending = false;
            UserDataPathProvider.IsRunningModded = true;
            throw;
        }
    }

    /// <summary>Called only by <see cref="DojoRunHistoryFlagRestorePatch"/>. Returns true (and clears the
    /// pending flag) only if the closing screen was one <see cref="Open"/> itself opened, so an unrelated
    /// NRunHistory close (there shouldn't be one — Run History is otherwise unreachable when modded, see
    /// CLAUDE.md §1) can't accidentally restore a flag Open() never touched.</summary>
    internal static bool ConsumeRestorePending()
    {
        if (!_restorePending)
        {
            return false;
        }
        _restorePending = false;
        return true;
    }
}

[HarmonyPatch(typeof(NSubmenu), nameof(NSubmenu.OnSubmenuClosed))]
public static class DojoRunHistoryFlagRestorePatch
{
    // ReSharper disable once UnusedMember.Global
    public static void Postfix(NSubmenu __instance)
    {
        if (__instance is NRunHistory && DojoRunBrowser.ConsumeRestorePending())
        {
            UserDataPathProvider.IsRunningModded = true;
        }
    }
}

/// <summary>Safety net for the launch-a-replay path, which tears down the main menu scene (and whatever
/// submenu was open on it) via a direct scene swap rather than by popping the submenu stack — see the class
/// docs on <see cref="DojoRunBrowser"/>. Patched broadly on the scene container itself (fires for every
/// scene swap in the game, not just Dojo ones) rather than narrowly on the launch path, so this can't be
/// missed by some other future code path that also swaps the current scene while the browser is open;
/// <see cref="DojoRunBrowser.ConsumeRestorePending"/> is a no-op unless the flag is actually pending.</summary>
[HarmonyPatch(typeof(NSceneContainer), nameof(NSceneContainer.SetCurrentScene))]
public static class DojoRunHistorySceneSwapRestorePatch
{
    // ReSharper disable once UnusedMember.Global
    public static void Prefix()
    {
        if (DojoRunBrowser.ConsumeRestorePending())
        {
            UserDataPathProvider.IsRunningModded = true;
        }
    }
}
