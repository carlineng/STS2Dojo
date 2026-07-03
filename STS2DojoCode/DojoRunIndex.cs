using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2Dojo.STS2DojoCode.Reconstruction;

namespace STS2Dojo.STS2DojoCode;

/// <summary>Everything the Dojo screen needs after scanning the real profile's history directory:
/// summarized (includable) runs plus counts of what was excluded and why, for future error-state UI.</summary>
public sealed class DojoRunIndexResult
{
    public required List<DojoRunSummary> Runs { get; init; }
    public required Dictionary<RunHistoryFileDecision, int> ExcludedCounts { get; init; }

    public int ExcludedTotal => ExcludedCounts.Values.Sum();
}

/// <summary>
/// Loads and summarizes the REAL (non-modded) profile's <c>.run</c> history files for the custom Dojo
/// run browser, entirely via direct read-only file access (<see cref="RunHistoryLoader"/> +
/// <see cref="LocalFileSaveStore"/>). Crucially, this NEVER touches
/// <c>UserDataPathProvider.IsRunningModded</c> — the real profile's path is computed by stripping the
/// <c>modded/</c> segment from the current profile's base path (see <see cref="RealProfilePath"/>),
/// so the §5h/§6 save-corruption hazard of flipping that global flag doesn't apply to the run LIST at
/// all. (The stock-NRunHistory drill-in path is the only remaining flag-flip user; see DojoRunBrowser.)
///
/// Results are cached per file keyed by last-write time: the first open of a ~1000-run profile pays a
/// one-time parse cost, later opens only parse files that changed (a finished run appends exactly one).
/// </summary>
public static class DojoRunIndex
{
    private sealed record CacheEntry(DateTime LastWriteUtc, RunHistoryFileDecision Decision, DojoRunSummary? Summary);

    private static readonly Dictionary<string, CacheEntry> Cache = new();

    /// <summary>The real profile's history directory as an absolute OS path, or null (logged) if it
    /// can't be resolved. Uses the modded session's own profile id — profile1's modded data maps to
    /// profile1's real data.</summary>
    public static string? TryGetRealHistoryDirectory()
    {
        try
        {
            int profileId = SaveManager.Instance.CurrentProfileId;
            string moddedBase = UserDataPathProvider.GetProfileScopedBasePath(profileId);
            string historyUserPath = RealProfilePath.BuildHistoryPathFromProfileBasePath(moddedBase);
            return ProjectSettings.GlobalizePath(historyUserPath);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not resolve the real profile's history directory: " + e);
            return null;
        }
    }

    /// <summary>Scans, loads, classifies (multiplayer/modifier/unsupported-schema/no-combat runs are
    /// excluded per CLAUDE.md §6/§10) and summarizes every includable run. Never throws — an unreadable
    /// directory yields an empty result, an unreadable file counts as LoadFailed.
    /// <paramref name="directory"/> should be resolved on the main thread (it comes from
    /// <see cref="TryGetRealHistoryDirectory"/>, which calls into Godot's ProjectSettings); everything in
    /// here is plain file/CPU work, safe to run inside Task.Run.</summary>
    public static DojoRunIndexResult LoadAll(string? directory)
    {
        var runs = new List<DojoRunSummary>();
        var excluded = new Dictionary<RunHistoryFileDecision, int>();

        string[] files;
        try
        {
            files = directory != null && Directory.Exists(directory)
                ? Directory.GetFiles(directory)
                : [];
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not list the real profile's history directory: " + e);
            files = [];
        }

        var seen = new HashSet<string>(files.Length);
        foreach (string file in files)
        {
            // Same skip set as the game's own RunHistorySaveManager.LoadAllRunHistoryNames.
            if (file.EndsWith(".backup", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".corrupt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            seen.Add(file);
            CacheEntry entry = ClassifyCached(file);
            if (entry.Summary != null)
            {
                runs.Add(entry.Summary);
            }
            else
            {
                excluded[entry.Decision] = excluded.GetValueOrDefault(entry.Decision) + 1;
            }
        }

        // Evict cache entries for files that no longer exist, so a deleted run doesn't pin its summary
        // (and its classification) for the rest of the session. Skipped when the listing itself failed —
        // an empty `files` from an I/O error must not wipe a perfectly good cache.
        if (files.Length > 0)
        {
            foreach (string stale in Cache.Keys.Where(key => !seen.Contains(key)).ToList())
            {
                Cache.Remove(stale);
            }
        }

        return new DojoRunIndexResult { Runs = runs, ExcludedCounts = excluded };
    }

    private static CacheEntry ClassifyCached(string file)
    {
        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(file);
        }
        catch (Exception)
        {
            lastWriteUtc = DateTime.MinValue;
        }

        if (Cache.TryGetValue(file, out CacheEntry? cached) && cached.LastWriteUtc == lastWriteUtc)
        {
            return cached;
        }

        CacheEntry entry = Classify(file, lastWriteUtc);
        Cache[file] = entry;
        return entry;
    }

    private static CacheEntry Classify(string file, DateTime lastWriteUtc)
    {
        RunHistoryFileCandidate candidate;
        try
        {
            candidate = new RunHistoryFileCandidate(file, RunHistoryLoader.Load(file));
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"[STS2Dojo] Skipping unloadable run history file '{file}': {e.Message}");
            candidate = new RunHistoryFileCandidate(file, LoadError: e);
        }

        RunHistoryFileSelection selection = RunHistoryFileSelector.Classify(candidate);
        if (selection.Decision != RunHistoryFileDecision.Include || selection.Run == null)
        {
            return new CacheEntry(lastWriteUtc, selection.Decision, null);
        }

        try
        {
            // runSource lets the summary re-read the file on demand instead of pinning the parsed
            // RunHistory graph in this session-lifetime cache — see DojoRunSummary.RunSource.
            return new CacheEntry(lastWriteUtc, selection.Decision,
                DojoRunSummarizer.Summarize(file, selection.Run, () => RunHistoryLoader.Load(file)));
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[STS2Dojo] Could not summarize run history file '{file}': {e}");
            return new CacheEntry(lastWriteUtc, RunHistoryFileDecision.LoadFailed, null);
        }
    }
}
