using System;
using System.IO;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

public sealed record RunHistoryFileCandidate(string Path, RunHistory? Run = null, Exception? LoadError = null);

public enum RunHistoryFileDecision
{
    Include,
    NotRunFile,
    LoadFailed,
    UnsupportedSchema,
    Multiplayer,
    ModifierRun,
    NoCombatFloors
}

public sealed class RunHistoryFileSelectionOptions
{
    public int MinSchemaVersion { get; init; } = 8;

    public int MaxSchemaVersion { get; init; } = 9;

    public bool ExcludeModifierRuns { get; init; } = true;

    public bool RequireCombatFloor { get; init; } = true;
}

public readonly record struct RunHistoryFileSelection(
    string Path,
    RunHistoryFileDecision Decision,
    RunHistory? Run);

public static class RunHistoryFileSelector
{
    public static RunHistoryFileSelection Classify(
        RunHistoryFileCandidate candidate,
        RunHistoryFileSelectionOptions? options = null)
    {
        options ??= new RunHistoryFileSelectionOptions();
        if (!string.Equals(System.IO.Path.GetExtension(candidate.Path), ".run", StringComparison.OrdinalIgnoreCase))
        {
            return new RunHistoryFileSelection(candidate.Path, RunHistoryFileDecision.NotRunFile, null);
        }

        if (candidate.LoadError != null || candidate.Run == null)
        {
            return new RunHistoryFileSelection(candidate.Path, RunHistoryFileDecision.LoadFailed, null);
        }

        RunHistory run = candidate.Run;
        if (run.SchemaVersion < options.MinSchemaVersion || run.SchemaVersion > options.MaxSchemaVersion)
        {
            return new RunHistoryFileSelection(candidate.Path, RunHistoryFileDecision.UnsupportedSchema, run);
        }

        if (!RunHistoryQueries.IsSinglePlayer(run))
        {
            return new RunHistoryFileSelection(candidate.Path, RunHistoryFileDecision.Multiplayer, run);
        }

        if (options.ExcludeModifierRuns && run.Modifiers.Count > 0)
        {
            return new RunHistoryFileSelection(candidate.Path, RunHistoryFileDecision.ModifierRun, run);
        }

        if (options.RequireCombatFloor && !RunHistoryQueries.HasAnyCombatFloor(run))
        {
            return new RunHistoryFileSelection(candidate.Path, RunHistoryFileDecision.NoCombatFloors, run);
        }

        return new RunHistoryFileSelection(candidate.Path, RunHistoryFileDecision.Include, run);
    }
}
