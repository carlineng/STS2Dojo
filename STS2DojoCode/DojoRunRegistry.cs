using System.Collections.Generic;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Tags which <see cref="RunState"/> instances are Dojo (throwaway, non-saving) runs, so combat-end and
/// save-suppression Harmony patches (<c>DojoCombatEndPatches.cs</c>) only intercept Dojo runs and never
/// touch a real player run. <see cref="RunState"/> has no free-form tag field, so this is a side-table.
/// A plain <see cref="HashSet{T}"/> is sufficient (not a weak table) because <see cref="RunManager"/> only
/// ever has one active <see cref="RunState"/> at a time, and it's explicitly unmarked on cleanup.
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
