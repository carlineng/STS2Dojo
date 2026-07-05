using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

public readonly record struct DojoRunFileFingerprint(
    string FilePath,
    string FileName,
    DateTime LastWriteUtc,
    long SizeBytes)
{
    public static bool TryRead(string filePath, out DojoRunFileFingerprint fingerprint)
    {
        try
        {
            var info = new FileInfo(filePath);
            fingerprint = new DojoRunFileFingerprint(
                info.FullName,
                info.Name,
                info.LastWriteTimeUtc,
                info.Exists ? info.Length : -1);
            return info.Exists;
        }
        catch
        {
            fingerprint = new DojoRunFileFingerprint(
                filePath,
                Path.GetFileName(filePath),
                DateTime.MinValue,
                -1);
            return false;
        }
    }
}

public sealed class DojoRunIndexCacheDocument
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = DojoRunIndexCache.SchemaVersion;

    [JsonPropertyName("entries")]
    public Dictionary<string, DojoRunIndexCacheEntry> Entries { get; set; } = [];
}

public sealed class DojoRunIndexCacheEntry
{
    [JsonPropertyName("cache_schema_version")]
    public int CacheSchemaVersion { get; set; } = DojoRunIndexCache.SchemaVersion;

    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("last_write_utc_ticks")]
    public long LastWriteUtcTicks { get; set; }

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("run_schema_version")]
    public int? RunSchemaVersion { get; set; }

    [JsonPropertyName("build_id")]
    public string? BuildId { get; set; }

    [JsonPropertyName("decision")]
    public RunHistoryFileDecision Decision { get; set; }

    [JsonPropertyName("summary")]
    public CachedDojoRunSummary? Summary { get; set; }

    [JsonPropertyName("eligibility_content_hash")]
    public string? EligibilityContentHash { get; set; }

    [JsonPropertyName("fight_eligibility")]
    public Dictionary<int, bool> FightEligibility { get; set; } = [];
}

public sealed class CachedDojoRunSummary
{
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("character_id")]
    public string CharacterId { get; set; } = "";

    [JsonPropertyName("ascension")]
    public int Ascension { get; set; }

    [JsonPropertyName("win")]
    public bool Win { get; set; }

    [JsonPropertyName("was_abandoned")]
    public bool WasAbandoned { get; set; }

    [JsonPropertyName("floors_reached")]
    public int FloorsReached { get; set; }

    [JsonPropertyName("max_act_reached")]
    public int MaxActReached { get; set; }

    [JsonPropertyName("end_hp")]
    public int EndHp { get; set; }

    [JsonPropertyName("end_max_hp")]
    public int EndMaxHp { get; set; }

    [JsonPropertyName("start_time")]
    public long StartTime { get; set; }

    [JsonPropertyName("run_time_seconds")]
    public float RunTimeSeconds { get; set; }

    [JsonPropertyName("seed")]
    public string Seed { get; set; } = "";

    [JsonPropertyName("killed_by_encounter_id")]
    public string? KilledByEncounterId { get; set; }

    [JsonPropertyName("killed_by_event_id")]
    public string? KilledByEventId { get; set; }

    [JsonPropertyName("deck_count")]
    public int DeckCount { get; set; }

    [JsonPropertyName("relic_count")]
    public int RelicCount { get; set; }

    [JsonPropertyName("relic_ids")]
    public List<string> RelicIds { get; set; } = [];

    [JsonPropertyName("deck_card_ids")]
    public List<string> DeckCardIds { get; set; } = [];

    [JsonPropertyName("acts")]
    public List<CachedDojoActSummary> Acts { get; set; } = [];
}

public sealed class CachedDojoActSummary
{
    [JsonPropertyName("act_index")]
    public int ActIndex { get; set; }

    [JsonPropertyName("act_id")]
    public string? ActId { get; set; }

    [JsonPropertyName("bosses")]
    public List<CachedDojoFightSummary> Bosses { get; set; } = [];

    [JsonPropertyName("elites")]
    public List<CachedDojoFightSummary> Elites { get; set; } = [];

    [JsonPropertyName("other_death_fights")]
    public List<CachedDojoFightSummary> OtherDeathFights { get; set; } = [];
}

public sealed class CachedDojoFightSummary
{
    [JsonPropertyName("global_floor")]
    public int GlobalFloor { get; set; }

    [JsonPropertyName("encounter_id")]
    public string EncounterId { get; set; } = "";

    [JsonPropertyName("room_type")]
    public string RoomType { get; set; } = "";

    [JsonPropertyName("display_id")]
    public string DisplayId { get; set; } = "";

    [JsonPropertyName("was_death_fight")]
    public bool WasDeathFight { get; set; }
}

public sealed record DojoRunIndexCacheHydration(
    RunHistoryFileDecision Decision,
    DojoRunSummary? Summary,
    int? RunSchemaVersion,
    string? BuildId,
    string? EligibilityContentHash);

public static class DojoRunIndexCache
{
    // Bumped 4 -> adds CachedDojoRunSummary.MaxActReached (3 added DeckCardIds, 2 added RelicIds). Load()
    // below discards the whole on-disk document on a version mismatch, forcing every run to be freshly
    // re-summarized (and re-cached) instead of silently hydrating pre-existing entries with a stale/zero
    // max-act value forever.
    public const int SchemaVersion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static DojoRunIndexCacheDocument Load(string? path, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new DojoRunIndexCacheDocument();
        }

        try
        {
            DojoRunIndexCacheDocument? document =
                JsonSerializer.Deserialize<DojoRunIndexCacheDocument>(File.ReadAllText(path), JsonOptions);
            if (document == null || document.SchemaVersion != SchemaVersion)
            {
                return new DojoRunIndexCacheDocument();
            }

            document.Entries ??= [];
            return document;
        }
        catch (Exception e)
        {
            log?.Invoke("[STS2Dojo] Could not read Dojo run index cache: " + e.Message);
            return new DojoRunIndexCacheDocument();
        }
    }

    public static bool SaveAtomic(string? path, DojoRunIndexCacheDocument document, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            document.SchemaVersion = SchemaVersion;
            File.WriteAllText(tempPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            log?.Invoke("[STS2Dojo] Could not write Dojo run index cache: " + e.Message);
            return false;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static bool TryHydrate(
        DojoRunIndexCacheEntry entry,
        DojoRunFileFingerprint fingerprint,
        string? currentEligibilityContentHash,
        Func<RunHistory> runSource,
        out DojoRunIndexCacheHydration hydration,
        out bool invalidatedEligibility)
    {
        hydration = new DojoRunIndexCacheHydration(
            RunHistoryFileDecision.LoadFailed, null, null, null, null);
        invalidatedEligibility = false;

        if (!Matches(entry, fingerprint))
        {
            return false;
        }

        if (entry.Decision != RunHistoryFileDecision.Include)
        {
            hydration = new DojoRunIndexCacheHydration(
                entry.Decision, null, entry.RunSchemaVersion, entry.BuildId, null);
            return true;
        }

        if (entry.Summary == null)
        {
            return false;
        }

        try
        {
            bool eligibilityValid =
                currentEligibilityContentHash != null
                && string.Equals(entry.EligibilityContentHash, currentEligibilityContentHash, StringComparison.Ordinal);
            Dictionary<int, bool> eligibility = eligibilityValid
                ? new Dictionary<int, bool>(entry.FightEligibility)
                : [];
            invalidatedEligibility = entry.FightEligibility.Count > 0 && !eligibilityValid;

            DojoRunSummary summary = HydrateSummary(entry.Summary, runSource, eligibility,
                eligibilityValid ? currentEligibilityContentHash : null);
            hydration = new DojoRunIndexCacheHydration(
                entry.Decision,
                summary,
                entry.RunSchemaVersion,
                entry.BuildId,
                eligibilityValid ? currentEligibilityContentHash : null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static DojoRunIndexCacheEntry FromResult(
        DojoRunFileFingerprint fingerprint,
        RunHistoryFileDecision decision,
        RunHistory? run,
        DojoRunSummary? summary,
        string? eligibilityContentHash,
        int? runSchemaVersion = null,
        string? buildId = null)
    {
        return new DojoRunIndexCacheEntry
        {
            CacheSchemaVersion = SchemaVersion,
            FilePath = fingerprint.FilePath,
            FileName = fingerprint.FileName,
            LastWriteUtcTicks = fingerprint.LastWriteUtc.Ticks,
            SizeBytes = fingerprint.SizeBytes,
            RunSchemaVersion = run?.SchemaVersion ?? runSchemaVersion,
            BuildId = run?.BuildId ?? buildId,
            Decision = decision,
            Summary = summary != null ? FromSummary(summary) : null,
            EligibilityContentHash =
                eligibilityContentHash != null && summary?.CachedFightEligibility.Count > 0
                    ? eligibilityContentHash
                    : null,
            FightEligibility = eligibilityContentHash != null
                && summary?.CachedFightEligibilityContentHash == eligibilityContentHash
                ? new Dictionary<int, bool>(summary.CachedFightEligibility)
                : []
        };
    }

    public static bool Upsert(
        DojoRunIndexCacheDocument document,
        DojoRunIndexCacheEntry entry)
    {
        string key = CacheKey(entry.FilePath);
        if (document.Entries.TryGetValue(key, out DojoRunIndexCacheEntry? existing)
            && AreEquivalent(existing, entry))
        {
            return false;
        }

        document.Entries[key] = entry;
        return true;
    }

    public static bool EvictMissing(
        DojoRunIndexCacheDocument document,
        ISet<string> seenFiles,
        string? scannedDirectory)
    {
        bool changed = false;
        if (string.IsNullOrWhiteSpace(scannedDirectory))
        {
            return false;
        }

        HashSet<string> normalizedSeen = seenFiles.Select(CacheKey).ToHashSet();
        foreach (string stale in document.Entries.Keys
                     .Where(key => IsInDirectory(key, scannedDirectory) && !normalizedSeen.Contains(CacheKey(key)))
                     .ToList())
        {
            document.Entries.Remove(stale);
            changed = true;
        }
        return changed;
    }

    public static bool TryGetEntry(
        DojoRunIndexCacheDocument document,
        string filePath,
        out DojoRunIndexCacheEntry entry) =>
        document.Entries.TryGetValue(CacheKey(filePath), out entry!);

    public static CachedDojoRunSummary FromSummary(DojoRunSummary summary)
    {
        return new CachedDojoRunSummary
        {
            FilePath = summary.FilePath,
            CharacterId = summary.CharacterId.ToString(),
            Ascension = summary.Ascension,
            Win = summary.Win,
            WasAbandoned = summary.WasAbandoned,
            FloorsReached = summary.FloorsReached,
            MaxActReached = summary.MaxActReached,
            EndHp = summary.EndHp,
            EndMaxHp = summary.EndMaxHp,
            StartTime = summary.StartTime,
            RunTimeSeconds = summary.RunTimeSeconds,
            Seed = summary.Seed,
            KilledByEncounterId = SerializeModelId(summary.KilledByEncounterId),
            KilledByEventId = SerializeModelId(summary.KilledByEventId),
            DeckCount = summary.DeckCount,
            RelicCount = summary.RelicCount,
            RelicIds = summary.RelicIds.Select(id => id.ToString()).ToList(),
            DeckCardIds = summary.DeckCardIds.Select(id => id.ToString()).ToList(),
            Acts = summary.Acts.Select(FromAct).ToList()
        };
    }

    private static DojoRunSummary HydrateSummary(
        CachedDojoRunSummary cached,
        Func<RunHistory> runSource,
        IReadOnlyDictionary<int, bool> fightEligibility,
        string? eligibilityContentHash)
    {
        var summary = new DojoRunSummary
        {
            FilePath = cached.FilePath,
            CharacterId = ModelId.Deserialize(cached.CharacterId),
            Ascension = cached.Ascension,
            Win = cached.Win,
            WasAbandoned = cached.WasAbandoned,
            FloorsReached = cached.FloorsReached,
            MaxActReached = cached.MaxActReached,
            EndHp = cached.EndHp,
            EndMaxHp = cached.EndMaxHp,
            StartTime = cached.StartTime,
            RunTimeSeconds = cached.RunTimeSeconds,
            Seed = cached.Seed,
            KilledByEncounterId = DeserializeModelIdOrNull(cached.KilledByEncounterId),
            KilledByEventId = DeserializeModelIdOrNull(cached.KilledByEventId),
            DeckCount = cached.DeckCount,
            RelicCount = cached.RelicCount,
            RelicIds = cached.RelicIds.Select(ModelId.Deserialize).ToList(),
            DeckCardIds = cached.DeckCardIds.Select(ModelId.Deserialize).ToList(),
            Acts = cached.Acts.Select(HydrateAct).ToList(),
            RunSource = runSource
        };
        summary.CachedFightEligibility = fightEligibility;
        summary.CachedFightEligibilityContentHash = eligibilityContentHash;
        return summary;
    }

    private static CachedDojoActSummary FromAct(DojoActSummary act)
    {
        return new CachedDojoActSummary
        {
            ActIndex = act.ActIndex,
            ActId = SerializeModelId(act.ActId),
            Bosses = act.Bosses.Select(FromFight).ToList(),
            Elites = act.Elites.Select(FromFight).ToList(),
            OtherDeathFights = act.OtherDeathFights.Select(FromFight).ToList()
        };
    }

    private static DojoActSummary HydrateAct(CachedDojoActSummary cached) =>
        new(
            cached.ActIndex,
            DeserializeModelIdOrNull(cached.ActId),
            cached.Bosses.Select(HydrateFight).ToList(),
            cached.Elites.Select(HydrateFight).ToList(),
            cached.OtherDeathFights.Select(HydrateFight).ToList());

    private static CachedDojoFightSummary FromFight(DojoFightSummary fight)
    {
        return new CachedDojoFightSummary
        {
            GlobalFloor = fight.GlobalFloor,
            EncounterId = fight.EncounterId.ToString(),
            RoomType = fight.RoomType.ToString(),
            DisplayId = fight.DisplayId.ToString(),
            WasDeathFight = fight.WasDeathFight
        };
    }

    private static DojoFightSummary HydrateFight(CachedDojoFightSummary cached)
    {
        if (!Enum.TryParse(cached.RoomType, ignoreCase: true, out RoomType roomType))
        {
            throw new JsonException("Unknown cached room type '" + cached.RoomType + "'.");
        }

        return new DojoFightSummary(
            cached.GlobalFloor,
            ModelId.Deserialize(cached.EncounterId),
            roomType,
            ModelId.Deserialize(cached.DisplayId),
            cached.WasDeathFight);
    }

    private static bool Matches(DojoRunIndexCacheEntry entry, DojoRunFileFingerprint fingerprint) =>
        entry.CacheSchemaVersion == SchemaVersion
        && string.Equals(CacheKey(entry.FilePath), CacheKey(fingerprint.FilePath), StringComparison.Ordinal)
        && string.Equals(entry.FileName, fingerprint.FileName, StringComparison.Ordinal)
        && entry.LastWriteUtcTicks == fingerprint.LastWriteUtc.Ticks
        && entry.SizeBytes == fingerprint.SizeBytes;

    private static bool AreEquivalent(DojoRunIndexCacheEntry left, DojoRunIndexCacheEntry right) =>
        left.CacheSchemaVersion == right.CacheSchemaVersion
        && string.Equals(left.FilePath, right.FilePath, StringComparison.Ordinal)
        && string.Equals(left.FileName, right.FileName, StringComparison.Ordinal)
        && left.LastWriteUtcTicks == right.LastWriteUtcTicks
        && left.SizeBytes == right.SizeBytes
        && left.RunSchemaVersion == right.RunSchemaVersion
        && string.Equals(left.BuildId, right.BuildId, StringComparison.Ordinal)
        && left.Decision == right.Decision
        && string.Equals(left.EligibilityContentHash, right.EligibilityContentHash, StringComparison.Ordinal)
        && AreDictionariesEquivalent(left.FightEligibility, right.FightEligibility)
        && AreSummariesEquivalent(left.Summary, right.Summary);

    private static bool AreSummariesEquivalent(CachedDojoRunSummary? left, CachedDojoRunSummary? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null)
        {
            return false;
        }

        return string.Equals(left.FilePath, right.FilePath, StringComparison.Ordinal)
            && string.Equals(left.CharacterId, right.CharacterId, StringComparison.Ordinal)
            && left.Ascension == right.Ascension
            && left.Win == right.Win
            && left.WasAbandoned == right.WasAbandoned
            && left.FloorsReached == right.FloorsReached
            && left.EndHp == right.EndHp
            && left.EndMaxHp == right.EndMaxHp
            && left.StartTime == right.StartTime
            && Math.Abs(left.RunTimeSeconds - right.RunTimeSeconds) < float.Epsilon
            && string.Equals(left.Seed, right.Seed, StringComparison.Ordinal)
            && string.Equals(left.KilledByEncounterId, right.KilledByEncounterId, StringComparison.Ordinal)
            && string.Equals(left.KilledByEventId, right.KilledByEventId, StringComparison.Ordinal)
            && left.DeckCount == right.DeckCount
            && left.RelicCount == right.RelicCount
            && left.RelicIds.SequenceEqual(right.RelicIds)
            && left.DeckCardIds.SequenceEqual(right.DeckCardIds)
            && AreActsEquivalent(left.Acts, right.Acts);
    }

    private static bool AreActsEquivalent(
        IReadOnlyList<CachedDojoActSummary> left,
        IReadOnlyList<CachedDojoActSummary> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            CachedDojoActSummary leftAct = left[i];
            CachedDojoActSummary rightAct = right[i];
            if (leftAct.ActIndex != rightAct.ActIndex
                || !string.Equals(leftAct.ActId, rightAct.ActId, StringComparison.Ordinal)
                || !AreFightsEquivalent(leftAct.Bosses, rightAct.Bosses)
                || !AreFightsEquivalent(leftAct.Elites, rightAct.Elites)
                || !AreFightsEquivalent(leftAct.OtherDeathFights, rightAct.OtherDeathFights))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreFightsEquivalent(
        IReadOnlyList<CachedDojoFightSummary> left,
        IReadOnlyList<CachedDojoFightSummary> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            CachedDojoFightSummary leftFight = left[i];
            CachedDojoFightSummary rightFight = right[i];
            if (leftFight.GlobalFloor != rightFight.GlobalFloor
                || !string.Equals(leftFight.EncounterId, rightFight.EncounterId, StringComparison.Ordinal)
                || !string.Equals(leftFight.RoomType, rightFight.RoomType, StringComparison.Ordinal)
                || !string.Equals(leftFight.DisplayId, rightFight.DisplayId, StringComparison.Ordinal)
                || leftFight.WasDeathFight != rightFight.WasDeathFight)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreDictionariesEquivalent(
        IReadOnlyDictionary<int, bool> left,
        IReadOnlyDictionary<int, bool> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach ((int floor, bool eligible) in left)
        {
            if (!right.TryGetValue(floor, out bool rightEligible) || rightEligible != eligible)
            {
                return false;
            }
        }

        return true;
    }

    private static string CacheKey(string filePath)
    {
        try
        {
            return string.IsNullOrWhiteSpace(filePath) ? filePath : Path.GetFullPath(filePath);
        }
        catch
        {
            return filePath;
        }
    }

    private static bool IsInDirectory(string filePath, string directory)
    {
        try
        {
            string? fileDirectory = Path.GetDirectoryName(CacheKey(filePath));
            return string.Equals(fileDirectory, CacheKey(directory), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? SerializeModelId(ModelId? id) => id?.ToString();

    private static ModelId? DeserializeModelIdOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ModelId.Deserialize(value);

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
            // Best-effort cleanup only.
        }
    }
}
