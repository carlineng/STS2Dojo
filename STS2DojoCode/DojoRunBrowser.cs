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
/// <c>SaveManager</c>/<c>NRunHistory</c> itself. The flag is restored via three independent hooks:
/// <list type="bullet">
/// <item>Backing out of the screen pops it off the submenu stack, firing <c>NSubmenu.OnSubmenuClosed</c> —
/// see <see cref="DojoRunHistoryFlagRestorePatch"/>.</item>
/// <item>Confirming a floor launches straight into the fight, which replaces the main menu scene entirely
/// via <c>NSceneContainer.SetCurrentScene</c> (it frees the old scene's children directly — see
/// <c>NSceneContainer.SetCurrentScene</c> in the decompiled source) WITHOUT ever popping the submenu stack
/// first, so <c>OnSubmenuClosed</c> never fires on this path. Without this hook, every successful replay
/// launch would permanently leave the whole modded session reading/writing the real profile. See
/// <see cref="DojoRunHistorySceneSwapRestorePatch"/>.</item>
/// <item><b>Hard safety net, added after a real incident:</b> the first two hooks only cover the two ways
/// <em>the Dojo itself</em> stops showing this screen. They don't cover the game quitting/restarting, or
/// the player reaching an unrelated screen (e.g. Settings, to disable mods) that saves something on its own
/// close, while this screen is still sitting open underneath — <c>NSettingsScreen.OnSubmenuClosed</c> calls
/// <c>SaveManager.Instance.SavePrefsFile()</c> unconditionally on every close, independent of quitting.
/// Either way, if <c>IsRunningModded</c> is still <c>false</c> when <em>any</em> profile-scoped save fires
/// (progress, profile, or prefs), it writes the CURRENT PROCESS's in-memory, modded-profile data (loaded at
/// launch) to the REAL profile's file — because the save PATH is resolved live from the flag, but the save
/// DATA is always whatever's in memory. This is exactly what happened: reaching Settings from an
/// unrestored Dojo browser and disabling mods silently overwrote the real profile's <c>progress.save</c>
/// with the modded session's near-empty progress, zeroing <c>Wins</c>/<c>Losses</c> and making
/// <c>SaveManager.IsCompendiumAvailable()</c> (literally <c>NumberOfRuns &gt; 0</c>) go false — the
/// Compendium vanishing from the main menu was the symptom that led here. Rather than trying to enumerate
/// every screen that might independently trigger a save, this hook patches the actual chokepoint: the three
/// profile-scoped save methods on <see cref="SaveManager"/> themselves, forcing the flag back to true right
/// before any of them runs, no matter who called it or why the flag was left false. See
/// <see cref="DojoRunHistorySaveSafetyPatch"/>.</item>
/// </list>
/// All hooks either call <see cref="ConsumeRestorePending"/> or set the flag directly; whichever fires
/// first wins and the others are no-ops.
/// </summary>
public static class DojoRunBrowser
{
    private static bool _restorePending;
    private static string? _targetRunFileName;

    /// <summary>Opens the stock run-history screen pre-selected on a specific run (by file name, e.g.
    /// "1779595721.run") — the custom Dojo screen's "View All Combats" drill-in. The selection itself
    /// happens in <see cref="DojoRunHistoryTargetRunPatch"/> once NRunHistory has built its run list.</summary>
    public static void OpenAtRun(NGame game, string runFileName)
    {
        _targetRunFileName = runFileName;
        try
        {
            Open(game);
        }
        finally
        {
            // Open() pushes NRunHistory synchronously, which fires OnSubmenuOpened (and the patch below)
            // before returning — if the target is still set here, the push never happened (no main menu,
            // empty history, exception) and it must not leak into some future unrelated open.
            _targetRunFileName = null;
        }
    }

    /// <summary>Called only by <see cref="DojoRunHistoryTargetRunPatch"/>.</summary>
    internal static string? ConsumeTargetRunFileName()
    {
        string? target = _targetRunFileName;
        _targetRunFileName = null;
        return target;
    }

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

/// <summary>
/// Selects a specific run when the Dojo opened NRunHistory via <see cref="DojoRunBrowser.OpenAtRun"/>.
/// NRunHistory.OnSubmenuOpened always rebuilds its file list and selects index 0 (newest); this postfix
/// re-selects the requested run by file name. The run-name list and the selection method are private,
/// so both are reached via AccessTools — the same pattern DojoMainMenuPatch uses for the main-menu
/// focus animation hooks.
/// </summary>
[HarmonyPatch(typeof(NRunHistory), nameof(NRunHistory.OnSubmenuOpened))]
public static class DojoRunHistoryTargetRunPatch
{
    private static readonly System.Reflection.FieldInfo RunNamesField =
        HarmonyLib.AccessTools.Field(typeof(NRunHistory), "_runNames");
    private static readonly System.Reflection.MethodInfo RefreshAndSelectRunMethod =
        HarmonyLib.AccessTools.Method(typeof(NRunHistory), "RefreshAndSelectRun");

    // ReSharper disable once UnusedMember.Global
    public static void Postfix(NRunHistory __instance)
    {
        string? target = DojoRunBrowser.ConsumeTargetRunFileName();
        if (target == null)
        {
            return;
        }

        try
        {
            if (RunNamesField.GetValue(__instance) is not System.Collections.Generic.List<string> runNames)
            {
                return;
            }

            int index = runNames.IndexOf(target);
            if (index <= 0)
            {
                // 0 = already selected by OnSubmenuOpened itself; -1 = not found (log and stay on newest).
                if (index < 0)
                {
                    MainFile.Logger.Error($"[STS2Dojo] Run '{target}' not found in the run history list.");
                }
                return;
            }

            if (RefreshAndSelectRunMethod.Invoke(__instance, new object[] { index })
                is System.Threading.Tasks.Task task)
            {
                MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(task);
            }
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not select the requested run in Run History: " + e);
        }
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

/// <summary>Unconditional hard safety net — see the "Hard safety net" bullet on <see cref="DojoRunBrowser"/>'s
/// class docs for the incident that motivated this. Patches the three profile-scoped save methods directly
/// (the actual chokepoint every persistence path funnels through — <c>NGame.Quit()</c>,
/// <c>NSettingsScreen.OnSubmenuClosed</c>, and anything else now or in the future) rather than trying to
/// patch every possible caller. Deliberately does NOT check <see cref="DojoRunBrowser.ConsumeRestorePending"/>
/// first (unlike the other two restore patches): in a modded session, <c>IsRunningModded</c> should always
/// be <c>true</c> by the time anything saves, full stop, regardless of whether the Dojo run browser was ever
/// opened this session or exactly how it was left. Forcing it here is always correct and never has a
/// downside — it only ever prevents a modded session from writing to the real profile, never the
/// reverse.</summary>
[HarmonyPatch(typeof(SaveManager))]
public static class DojoRunHistorySaveSafetyPatch
{
    [HarmonyPatch(nameof(SaveManager.SaveProgressFile))]
    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Global
    public static void SaveProgressFilePrefix() => RestoreFlag();

    [HarmonyPatch(nameof(SaveManager.SaveProfile))]
    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Global
    public static void SaveProfilePrefix() => RestoreFlag();

    [HarmonyPatch(nameof(SaveManager.SavePrefsFile))]
    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Global
    public static void SavePrefsFilePrefix() => RestoreFlag();

    private static void RestoreFlag()
    {
        DojoRunBrowser.ConsumeRestorePending();
        UserDataPathProvider.IsRunningModded = true;
    }
}
