using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Tags which <see cref="RunState"/> instances are Dojo (throwaway, non-saving) runs, so combat-end and
/// save-suppression Harmony patches (<c>DojoCombatEndPatches.cs</c>) only intercept Dojo runs and never
/// touch a real player run. <see cref="RunState"/> has no free-form tag field, so this is a side-table.
/// A plain <see cref="HashSet{T}"/> is sufficient (not a weak table) because <see cref="RunManager"/> only
/// ever has one active <see cref="RunState"/> at a time, and entries are removed automatically the moment
/// that run is torn down — see <see cref="DojoUnmarkOnCleanUpPatch"/> below. Without that, a HashSet would
/// retain every Dojo RunState (and its whole map/deck/history object graph) for the life of the process,
/// since nothing else would ever remove an entry (e.g. a long <c>dojoagain</c> retry loop).
/// </summary>
public static class DojoRunRegistry
{
    private static readonly HashSet<RunState> DojoRuns = new();

    public static void MarkAsDojo(RunState state) => DojoRuns.Add(state);

    public static void Unmark(RunState state) => DojoRuns.Remove(state);

    public static bool IsDojo(RunState? state) => state != null && DojoRuns.Contains(state);

    /// <summary>Is the RunManager's CURRENT run (if any) a Dojo run? Used from Harmony patches, which
    /// don't have a RunState parameter handy at their patch points.</summary>
    public static bool IsCurrentRunDojo() => IsDojo(RunManager.Instance.DebugOnlyGetState());
}

/// <summary>
/// Every path that ends a run — the game's own <c>NGame.ReturnToMainMenu</c> (used by the Dojo's win/loss
/// redirect), the Dojo's own "clean up a stale run before relaunching" guard, and the Dojo's own
/// failure-recovery cleanup (<c>DojoLaunch.LaunchInternal</c>'s catch block) — funnels through
/// <c>RunManager.CleanUp()</c>. Unmarking here, once, centrally, means no caller of
/// <c>RunManager.Instance.CleanUp()</c> needs to remember to also call <see cref="DojoRunRegistry.Unmark"/>
/// — it can't be forgotten at a future call site. Runs as a Prefix (before <c>State</c> is nulled in
/// <c>CleanUp</c>'s <c>finally</c> block) so the state-to-unmark is still readable.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class DojoUnmarkOnCleanUpPatch
{
    // ReSharper disable once UnusedMember.Global
    public static void Prefix()
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state != null)
        {
            DojoRunRegistry.Unmark(state);
        }
    }
}
