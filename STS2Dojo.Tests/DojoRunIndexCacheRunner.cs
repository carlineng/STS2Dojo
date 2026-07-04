using MegaCrit.Sts2.Core.Runs;
using STS2Dojo.STS2DojoCode.Reconstruction;

internal static class DojoRunIndexCacheRunner
{
    public static void Run()
    {
        Run("cache hit and JSON round-trip", CacheHitAndJsonRoundTrip);
        Run("changed file invalidation", ChangedFileInvalidation);
        Run("stale file eviction", StaleFileEviction);
        Run("eligibility hash invalidation", EligibilityHashInvalidation);
        Run("hydration equivalence", HydrationEquivalence);

        Console.WriteLine();
        Console.WriteLine("5 Dojo run-index cache test groups passed.");
    }

    private static void Run(string name, Action test)
    {
        test();
        Console.WriteLine("PASS " + name);
    }

    private static void CacheHitAndJsonRoundTrip()
    {
        string path = RunPath("1782524433.run");
        RunHistory run = TestRunHistoryLoader.Load(path);
        DojoRunSummary fresh = DojoRunSummarizer.Summarize(path, run);
        fresh.CachedFightEligibility = new Dictionary<int, bool> { [48] = true, [49] = false };
        fresh.CachedFightEligibilityContentHash = "hash-a";

        Assert.True(DojoRunFileFingerprint.TryRead(path, out DojoRunFileFingerprint fingerprint),
            "fixture fingerprint");
        DojoRunIndexCacheEntry entry =
            DojoRunIndexCache.FromResult(fingerprint, RunHistoryFileDecision.Include, run, fresh, "hash-a");
        var document = new DojoRunIndexCacheDocument();
        Assert.True(DojoRunIndexCache.Upsert(document, entry), "entry upserted");

        string cachePath = Path.Combine(Path.GetTempPath(), "sts2dojo-cache-test-" + Guid.NewGuid() + ".json");
        try
        {
            Assert.True(DojoRunIndexCache.SaveAtomic(cachePath, document), "cache saved");
            DojoRunIndexCacheDocument loaded = DojoRunIndexCache.Load(cachePath);
            Assert.True(DojoRunIndexCache.TryGetEntry(loaded, path, out DojoRunIndexCacheEntry loadedEntry),
                "round-tripped entry found");
            Assert.True(DojoRunIndexCache.TryHydrate(
                    loadedEntry,
                    fingerprint,
                    "hash-a",
                    () => throw new TestFailureException("cache hit should not load the run until GetRun"),
                    out DojoRunIndexCacheHydration hydration,
                    out bool invalidatedEligibility),
                "matching entry hydrates");

            Assert.True(!invalidatedEligibility, "eligibility remains valid");
            Assert.Equal(RunHistoryFileDecision.Include, hydration.Decision, "hydrated decision");
            Assert.Equal("CHARACTER.IRONCLAD", hydration.Summary?.CharacterId.ToString(), "hydrated character");
            Assert.True(hydration.Summary?.CachedFightEligibility[48] == true, "floor 48 eligibility");
            Assert.True(hydration.Summary?.CachedFightEligibility[49] == false, "floor 49 eligibility");
        }
        finally
        {
            TryDelete(cachePath);
        }
    }

    private static void ChangedFileInvalidation()
    {
        string path = RunPath("1782524433.run");
        RunHistory run = TestRunHistoryLoader.Load(path);
        DojoRunSummary summary = DojoRunSummarizer.Summarize(path, run);
        Assert.True(DojoRunFileFingerprint.TryRead(path, out DojoRunFileFingerprint fingerprint),
            "fixture fingerprint");

        DojoRunIndexCacheEntry entry =
            DojoRunIndexCache.FromResult(fingerprint, RunHistoryFileDecision.Include, run, summary, "hash-a");
        DojoRunFileFingerprint changedSize =
            fingerprint with { SizeBytes = fingerprint.SizeBytes + 1 };
        DojoRunFileFingerprint changedMtime =
            fingerprint with { LastWriteUtc = fingerprint.LastWriteUtc.AddSeconds(1) };

        Assert.True(!DojoRunIndexCache.TryHydrate(
                entry,
                changedSize,
                "hash-a",
                () => run,
                out _,
                out _),
            "size change invalidates");
        Assert.True(!DojoRunIndexCache.TryHydrate(
                entry,
                changedMtime,
                "hash-a",
                () => run,
                out _,
                out _),
            "mtime change invalidates");
    }

    private static void StaleFileEviction()
    {
        string keep = RunPath("1782524433.run");
        string stale = RunPath("1779595721.run");
        string otherProfile = Path.Combine(
            Path.GetTempPath(), "sts2dojo-other-profile", "history", "1782524433.run");
        Assert.True(DojoRunFileFingerprint.TryRead(keep, out DojoRunFileFingerprint keepFingerprint),
            "keep fingerprint");
        Assert.True(DojoRunFileFingerprint.TryRead(stale, out DojoRunFileFingerprint staleFingerprint),
            "stale fingerprint");
        DojoRunFileFingerprint otherProfileFingerprint =
            keepFingerprint with { FilePath = otherProfile, FileName = Path.GetFileName(otherProfile) };

        var document = new DojoRunIndexCacheDocument();
        Assert.True(DojoRunIndexCache.Upsert(document, DojoRunIndexCache.FromResult(
            keepFingerprint, RunHistoryFileDecision.LoadFailed, null, null, null)), "keep upsert");
        Assert.True(DojoRunIndexCache.Upsert(document, DojoRunIndexCache.FromResult(
            staleFingerprint, RunHistoryFileDecision.LoadFailed, null, null, null)), "stale upsert");
        Assert.True(DojoRunIndexCache.Upsert(document, DojoRunIndexCache.FromResult(
            otherProfileFingerprint, RunHistoryFileDecision.LoadFailed, null, null, null)), "other profile upsert");

        Assert.True(DojoRunIndexCache.EvictMissing(
                document,
                new HashSet<string> { keep },
                Path.GetDirectoryName(keep)),
            "eviction changed document");
        Assert.True(DojoRunIndexCache.TryGetEntry(document, keep, out _), "kept entry remains");
        Assert.True(!DojoRunIndexCache.TryGetEntry(document, stale, out _), "stale entry removed");
        Assert.True(DojoRunIndexCache.TryGetEntry(document, otherProfile, out _),
            "other profile entry remains");
    }

    private static void EligibilityHashInvalidation()
    {
        string path = RunPath("1779595721.run");
        RunHistory run = TestRunHistoryLoader.Load(path);
        DojoRunSummary summary = DojoRunSummarizer.Summarize(path, run);
        summary.CachedFightEligibility = new Dictionary<int, bool> { [25] = true };
        summary.CachedFightEligibilityContentHash = "old-hash";
        Assert.True(DojoRunFileFingerprint.TryRead(path, out DojoRunFileFingerprint fingerprint),
            "fixture fingerprint");
        DojoRunIndexCacheEntry entry =
            DojoRunIndexCache.FromResult(fingerprint, RunHistoryFileDecision.Include, run, summary, "old-hash");

        Assert.True(DojoRunIndexCache.TryHydrate(
                entry,
                fingerprint,
                "new-hash",
                () => throw new TestFailureException("summary hydration should not load full run"),
                out DojoRunIndexCacheHydration hydration,
                out bool invalidatedEligibility),
            "summary still hydrates after content hash changes");

        Assert.True(invalidatedEligibility, "eligibility invalidated");
        Assert.Equal("CHARACTER.SILENT", hydration.Summary?.CharacterId.ToString(), "summary retained");
        Assert.Equal(0, hydration.Summary?.CachedFightEligibility.Count ?? -1, "eligibility cleared");
        Assert.Equal(null, hydration.Summary?.CachedFightEligibilityContentHash, "eligibility hash cleared");
    }

    private static void HydrationEquivalence()
    {
        string path = RunPath("1782696823.run");
        RunHistory run = TestRunHistoryLoader.Load(path);
        DojoRunSummary fresh = DojoRunSummarizer.Summarize(path, run);
        fresh.CachedFightEligibility = new Dictionary<int, bool> { [49] = true };
        fresh.CachedFightEligibilityContentHash = "hash-a";
        Assert.True(DojoRunFileFingerprint.TryRead(path, out DojoRunFileFingerprint fingerprint),
            "fixture fingerprint");
        DojoRunIndexCacheEntry entry =
            DojoRunIndexCache.FromResult(fingerprint, RunHistoryFileDecision.Include, run, fresh, "hash-a");

        Assert.True(DojoRunIndexCache.TryHydrate(
                entry,
                fingerprint,
                "hash-a",
                () => TestRunHistoryLoader.Load(path),
                out DojoRunIndexCacheHydration hydration,
                out _),
            "entry hydrates");
        DojoRunSummary hydrated =
            hydration.Summary ?? throw new TestFailureException("missing hydrated summary");

        AssertSummariesEqual(fresh, hydrated);
        Assert.True(hydrated.CachedFightEligibility[49], "eligibility hydrates");
    }

    private static void AssertSummariesEqual(DojoRunSummary expected, DojoRunSummary actual)
    {
        Assert.Equal(expected.FilePath, actual.FilePath, "file path");
        Assert.Equal(expected.CharacterId.ToString(), actual.CharacterId.ToString(), "character");
        Assert.Equal(expected.Ascension, actual.Ascension, "ascension");
        Assert.Equal(expected.Win, actual.Win, "win");
        Assert.Equal(expected.WasAbandoned, actual.WasAbandoned, "abandoned");
        Assert.Equal(expected.FloorsReached, actual.FloorsReached, "floors reached");
        Assert.Equal(expected.EndHp, actual.EndHp, "end hp");
        Assert.Equal(expected.EndMaxHp, actual.EndMaxHp, "end max hp");
        Assert.Equal(expected.StartTime, actual.StartTime, "start time");
        Assert.Equal(expected.RunTimeSeconds, actual.RunTimeSeconds, "run time");
        Assert.Equal(expected.Seed, actual.Seed, "seed");
        Assert.Equal(expected.KilledByEncounterId?.ToString(), actual.KilledByEncounterId?.ToString(),
            "killed by encounter");
        Assert.Equal(expected.KilledByEventId?.ToString(), actual.KilledByEventId?.ToString(),
            "killed by event");
        Assert.Equal(expected.DeckCount, actual.DeckCount, "deck count");
        Assert.Equal(expected.RelicCount, actual.RelicCount, "relic count");
        Assert.SequenceEqual(
            expected.RelicIds.Select(id => id.ToString()).ToArray(),
            actual.RelicIds.Select(id => id.ToString()).ToArray(),
            "relic ids");
        Assert.Equal(expected.Acts.Count, actual.Acts.Count, "act count");

        for (int i = 0; i < expected.Acts.Count; i++)
        {
            DojoActSummary expectedAct = expected.Acts[i];
            DojoActSummary actualAct = actual.Acts[i];
            Assert.Equal(expectedAct.ActIndex, actualAct.ActIndex, "act index " + i);
            Assert.Equal(expectedAct.ActId?.ToString(), actualAct.ActId?.ToString(), "act id " + i);
            AssertFightsEqual(expectedAct.Bosses, actualAct.Bosses, "bosses " + i);
            AssertFightsEqual(expectedAct.Elites, actualAct.Elites, "elites " + i);
            AssertFightsEqual(expectedAct.OtherDeathFights, actualAct.OtherDeathFights, "death fights " + i);
        }
    }

    private static void AssertFightsEqual(
        IReadOnlyList<DojoFightSummary> expected,
        IReadOnlyList<DojoFightSummary> actual,
        string label)
    {
        Assert.Equal(expected.Count, actual.Count, label + " count");
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].GlobalFloor, actual[i].GlobalFloor, label + " floor " + i);
            Assert.Equal(expected[i].EncounterId.ToString(), actual[i].EncounterId.ToString(),
                label + " encounter " + i);
            Assert.Equal(expected[i].RoomType, actual[i].RoomType, label + " room " + i);
            Assert.Equal(expected[i].DisplayId.ToString(), actual[i].DisplayId.ToString(),
                label + " display " + i);
            Assert.Equal(expected[i].WasDeathFight, actual[i].WasDeathFight, label + " death " + i);
        }
    }

    private static string RunPath(string runFile) =>
        Path.Combine(TestRunner.FindRepoRoot(), "runfiles", runFile);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Test cleanup only.
        }
    }
}
