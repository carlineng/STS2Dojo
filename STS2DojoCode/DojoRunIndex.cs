using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
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
/// so the §5h/§6 save-corruption hazard of flipping that global flag doesn't apply. Nothing in the Dojo
/// flips that flag anymore now that the stock-NRunHistory drill-in is gone (replaced by the in-row floor
/// map — see <see cref="DojoRunRow"/>); only the unconditional <c>DojoRunHistorySaveSafetyPatch</c> remains.
///
/// Results are cached per file by path + mtime + size, first in process memory and then in a mod-owned
/// sidecar under Godot user data. The first open of a ~1000-run profile pays the parse cost; warm launches
/// hydrate summaries from JSON and only parse files that changed.
/// </summary>
public static class DojoRunIndex
{
    private const int EligibilitySaveDebounceMs = 500;

    private sealed record CacheEntry(
        DojoRunFileFingerprint Fingerprint,
        RunHistoryFileDecision Decision,
        DojoRunSummary? Summary,
        int? RunSchemaVersion,
        string? BuildId,
        string? EligibilityContentHash);

    private static readonly Dictionary<string, CacheEntry> Cache = new();
    private static readonly object EligibilitySaveLock = new();
    private static readonly Dictionary<string, DojoRunIndexCacheEntry> PendingEligibilityCacheEntries = new();
    private static bool EligibilitySaveScheduled;
    private static string? EligibilitySaveCachePath;

    public static string? TryGetCachePath()
    {
        try
        {
            return ProjectSettings.GlobalizePath("user://sts2dojo/run-index-v1.json");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Could not resolve the Dojo run index cache path: " + e);
            return null;
        }
    }

    public static string? TryGetEligibilityContentHash()
    {
        try
        {
            return ModelIdSerializationCache.Hash.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            MainFile.Logger.Info("[STS2Dojo] Could not read ModelIdSerializationCache.Hash: " + e.Message);
            return null;
        }
    }

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
    public static DojoRunIndexResult LoadAll(
        string? directory,
        string? cachePath = null,
        string? eligibilityContentHash = null)
    {
        var runs = new List<DojoRunSummary>();
        var excluded = new Dictionary<RunHistoryFileDecision, int>();
        DojoRunIndexCacheDocument diskCache = DojoRunIndexCache.Load(
            cachePath, message => MainFile.Logger.Info(message));
        bool diskCacheChanged = false;

        string[] files;
        bool listedSuccessfully = false;
        try
        {
            files = directory != null && Directory.Exists(directory)
                ? Directory.GetFiles(directory)
                : [];
            listedSuccessfully = directory != null && Directory.Exists(directory);
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
            CacheEntry entry = ClassifyCached(file, diskCache, eligibilityContentHash, ref diskCacheChanged);
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
        // (and its classification) for the rest of the session. Skipped when the listing itself failed,
        // because an empty `files` from an I/O error must not wipe a perfectly good cache.
        if (listedSuccessfully)
        {
            foreach (string stale in Cache.Keys
                         .Where(key => IsFileInDirectory(key, directory) && !seen.Contains(key))
                         .ToList())
            {
                Cache.Remove(stale);
            }

            if (DojoRunIndexCache.EvictMissing(diskCache, seen, directory))
            {
                diskCacheChanged = true;
            }
        }

        if (diskCacheChanged)
        {
            DojoRunIndexCache.SaveAtomic(cachePath, diskCache, message => MainFile.Logger.Info(message));
        }

        return new DojoRunIndexResult { Runs = runs, ExcludedCounts = excluded };
    }

    private static CacheEntry ClassifyCached(
        string file,
        DojoRunIndexCacheDocument diskCache,
        string? eligibilityContentHash,
        ref bool diskCacheChanged)
    {
        if (!DojoRunFileFingerprint.TryRead(file, out DojoRunFileFingerprint fingerprint))
        {
            fingerprint = new DojoRunFileFingerprint(file, Path.GetFileName(file), DateTime.MinValue, -1);
        }

        if (Cache.TryGetValue(file, out CacheEntry? cached)
            && cached.Fingerprint.LastWriteUtc == fingerprint.LastWriteUtc
            && cached.Fingerprint.SizeBytes == fingerprint.SizeBytes
            && cached.Fingerprint.FileName == fingerprint.FileName)
        {
            if (cached.Summary != null
                && cached.EligibilityContentHash != null
                && !string.Equals(cached.EligibilityContentHash, eligibilityContentHash, StringComparison.Ordinal))
            {
                cached.Summary.CachedFightEligibility = new Dictionary<int, bool>();
                cached.Summary.CachedFightEligibilityContentHash = null;
                cached = cached with { EligibilityContentHash = null };
                Cache[file] = cached;
            }

            diskCacheChanged |= DojoRunIndexCache.Upsert(diskCache,
                DojoRunIndexCache.FromResult(
                    fingerprint,
                    cached.Decision,
                    null,
                    cached.Summary,
                    cached.EligibilityContentHash,
                    cached.RunSchemaVersion,
                    cached.BuildId));
            return cached;
        }

        if (DojoRunIndexCache.TryGetEntry(diskCache, file, out DojoRunIndexCacheEntry diskEntry)
            && DojoRunIndexCache.TryHydrate(
                diskEntry,
                fingerprint,
                eligibilityContentHash,
                () => RunHistoryLoader.Load(file),
                out DojoRunIndexCacheHydration hydration,
                out bool invalidatedEligibility))
        {
            if (invalidatedEligibility)
            {
                diskEntry.EligibilityContentHash = null;
                diskEntry.FightEligibility = [];
                diskCacheChanged = true;
            }

            var hydratedEntry = new CacheEntry(
                fingerprint,
                hydration.Decision,
                hydration.Summary,
                hydration.RunSchemaVersion,
                hydration.BuildId,
                hydration.EligibilityContentHash);
            Cache[file] = hydratedEntry;
            return hydratedEntry;
        }

        CacheEntry entry = Classify(file, fingerprint);
        Cache[file] = entry;
        diskCacheChanged |= DojoRunIndexCache.Upsert(diskCache,
            DojoRunIndexCache.FromResult(
                fingerprint,
                entry.Decision,
                null,
                entry.Summary,
                null,
                entry.RunSchemaVersion,
                entry.BuildId));
        return entry;
    }

    private static CacheEntry Classify(
        string file,
        DojoRunFileFingerprint fingerprint)
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
            return new CacheEntry(
                fingerprint,
                selection.Decision,
                null,
                selection.Run?.SchemaVersion,
                selection.Run?.BuildId,
                null);
        }

        try
        {
            // runSource lets the summary re-read the file on demand instead of pinning the parsed
            // RunHistory graph in this session-lifetime cache — see DojoRunSummary.RunSource.
            DojoRunSummary summary =
                DojoRunSummarizer.Summarize(file, selection.Run, () => RunHistoryLoader.Load(file));

            return new CacheEntry(
                fingerprint,
                selection.Decision,
                summary,
                selection.Run.SchemaVersion,
                selection.Run.BuildId,
                null);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[STS2Dojo] Could not summarize run history file '{file}': {e}");
            return new CacheEntry(fingerprint, RunHistoryFileDecision.LoadFailed, null, null, null, null);
        }
    }

    public static void RememberFightEligibility(DojoRunSummary summary, int globalFloor, bool eligible)
    {
        string? eligibilityContentHash = TryGetEligibilityContentHash();
        if (eligibilityContentHash == null)
        {
            return;
        }

        try
        {
            Dictionary<int, bool> eligibility = summary.CachedFightEligibilityContentHash == eligibilityContentHash
                ? new Dictionary<int, bool>(summary.CachedFightEligibility)
                : [];
            if (eligibility.TryGetValue(globalFloor, out bool cached) && cached == eligible)
            {
                return;
            }

            eligibility[globalFloor] = eligible;
            summary.CachedFightEligibility = eligibility;
            summary.CachedFightEligibilityContentHash = eligibilityContentHash;

            if (!DojoRunFileFingerprint.TryRead(summary.FilePath, out DojoRunFileFingerprint fingerprint))
            {
                return;
            }

            Cache.TryGetValue(summary.FilePath, out CacheEntry? cachedEntry);
            int? runSchemaVersion = cachedEntry?.RunSchemaVersion;
            string? buildId = cachedEntry?.BuildId;
            Cache[summary.FilePath] = new CacheEntry(
                fingerprint,
                RunHistoryFileDecision.Include,
                summary,
                runSchemaVersion,
                buildId,
                eligibilityContentHash);

            string? cachePath = TryGetCachePath();
            QueueEligibilityCacheSave(cachePath, DojoRunIndexCache.FromResult(
                fingerprint,
                RunHistoryFileDecision.Include,
                null,
                summary,
                eligibilityContentHash,
                runSchemaVersion,
                buildId));
        }
        catch (Exception e)
        {
            MainFile.Logger.Info("[STS2Dojo] Could not persist Dojo fight eligibility: " + e.Message);
        }
    }

    private static void QueueEligibilityCacheSave(string? cachePath, DojoRunIndexCacheEntry entry)
    {
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return;
        }

        lock (EligibilitySaveLock)
        {
            PendingEligibilityCacheEntries[entry.FilePath] = entry;
            EligibilitySaveCachePath = cachePath;
            if (EligibilitySaveScheduled)
            {
                return;
            }

            EligibilitySaveScheduled = true;
        }

        TaskHelper.RunSafely(FlushEligibilityCacheSavesAsync());
    }

    private static async Task FlushEligibilityCacheSavesAsync()
    {
        while (true)
        {
            await Task.Delay(EligibilitySaveDebounceMs);

            Dictionary<string, DojoRunIndexCacheEntry> pending;
            string? cachePath;
            lock (EligibilitySaveLock)
            {
                pending = new Dictionary<string, DojoRunIndexCacheEntry>(PendingEligibilityCacheEntries);
                PendingEligibilityCacheEntries.Clear();
                cachePath = EligibilitySaveCachePath;
                EligibilitySaveCachePath = null;
            }

            if (pending.Count > 0 && !string.IsNullOrWhiteSpace(cachePath))
            {
                try
                {
                    DojoRunIndexCacheDocument diskCache = DojoRunIndexCache.Load(
                        cachePath, message => MainFile.Logger.Info(message));
                    bool changed = false;
                    foreach (DojoRunIndexCacheEntry entry in pending.Values)
                    {
                        changed |= DojoRunIndexCache.Upsert(diskCache, entry);
                    }

                    if (changed)
                    {
                        DojoRunIndexCache.SaveAtomic(cachePath, diskCache, message => MainFile.Logger.Info(message));
                    }
                }
                catch (Exception e)
                {
                    MainFile.Logger.Info("[STS2Dojo] Could not flush Dojo fight eligibility cache: " + e.Message);
                }
            }

            lock (EligibilitySaveLock)
            {
                if (PendingEligibilityCacheEntries.Count == 0)
                {
                    EligibilitySaveScheduled = false;
                    return;
                }
            }
        }
    }

    private static bool IsFileInDirectory(string filePath, string? directory)
    {
        if (directory == null)
        {
            return false;
        }

        try
        {
            string? fileDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return string.Equals(
                fileDirectory,
                Path.GetFullPath(directory),
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
