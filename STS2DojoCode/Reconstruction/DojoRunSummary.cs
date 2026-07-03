using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

/// <summary>
/// One highlighted fight in a run, as shown on a Dojo run-browser row's per-act strip. Pure data — the
/// display name and live content-eligibility of the encounter are resolved by the UI layer at row-build
/// time (see DojoFloorEligibility), not here, so this stays unit-testable against DTO test doubles.
/// <c>WasDeathFight</c> is true for the fight the player died in (lost, non-abandoned runs only): the
/// run's last visited floor whose combat room matches killed_by_encounter.
/// </summary>
public sealed record DojoFightSummary(
    int GlobalFloor,
    ModelId EncounterId,
    RoomType RoomType,
    ModelId DisplayId,
    bool WasDeathFight);

/// <summary>Per-act slice of the fights shown on a compact Dojo row. ActId comes from the run file's top-level
/// acts list and can be null for acts beyond that list's length (defensive; not seen in the corpus).</summary>
public sealed record DojoActSummary(
    int ActIndex,
    ModelId? ActId,
    IReadOnlyList<DojoFightSummary> Bosses,
    IReadOnlyList<DojoFightSummary> Elites,
    IReadOnlyList<DojoFightSummary> OtherDeathFights)
{
    public IEnumerable<DojoFightSummary> DisplayFights => Bosses.Concat(Elites).Concat(OtherDeathFights);
}

/// <summary>
/// Everything a Dojo run-browser row displays about one <c>.run</c> file, precomputed once at load time
/// (CLAUDE.md §9 item 5's custom-browser follow-up). Pure data derived from <see cref="RunHistory"/>
/// fields only — no ModelDb/localization access — so it is unit-testable against the DTO test doubles.
/// </summary>
public sealed class DojoRunSummary
{
    public required string FilePath { get; init; }
    public required ModelId CharacterId { get; init; }
    public required int Ascension { get; init; }
    public required bool Win { get; init; }
    public required bool WasAbandoned { get; init; }
    public required int FloorsReached { get; init; }
    public required int EndHp { get; init; }
    public required int EndMaxHp { get; init; }
    public required long StartTime { get; init; }
    public required float RunTimeSeconds { get; init; }
    public required string Seed { get; init; }
    public required int DeckCount { get; init; }
    public required int RelicCount { get; init; }
    public required IReadOnlyList<DojoActSummary> Acts { get; init; }

    /// <summary>Re-produces the full parsed run when a row is built or a fight is launched. Deliberately
    /// a factory + weak cache rather than a strong <c>RunHistory</c> reference: the summaries for an
    /// entire profile (~1000 runs) live for the whole session in DojoRunIndex's cache, and pinning every
    /// parsed run graph (full per-floor history, deck/relic lists) would cost tens to hundreds of MB.
    /// With the weak cache, only runs whose rows are currently built stay loaded.</summary>
    public required Func<RunHistory> RunSource { get; init; }

    private WeakReference<RunHistory>? _cachedRun;

    /// <summary>The full parsed run for this summary (re-read from disk if it has been collected since
    /// the last use). Can throw if the underlying file has become unreadable — callers on UI paths
    /// should treat that as a degraded row, not a crash.</summary>
    public RunHistory GetRun()
    {
        if (_cachedRun != null && _cachedRun.TryGetTarget(out RunHistory? cached))
        {
            return cached;
        }

        RunHistory run = RunSource();
        _cachedRun = new WeakReference<RunHistory>(run);
        return run;
    }

    /// <summary>Seeds the weak cache with the instance the summarizer already parsed, so the first
    /// GetRun() after building the index doesn't immediately re-read the file it came from.</summary>
    internal void SeedRunCache(RunHistory run) => _cachedRun = new WeakReference<RunHistory>(run);
}

public static class DojoRunSummarizer
{
    /// <summary>
    /// Builds the row summary for a single-player run. Caller is responsible for having already gated
    /// the run through <see cref="RunHistoryFileSelector"/> (multiplayer/modifier/no-combat exclusions) —
    /// this only distills display data and assumes players[0] is the (sole) player.
    /// <paramref name="runSource"/> re-produces the run on demand (see DojoRunSummary.RunSource); when
    /// omitted, the summary just retains <paramref name="run"/> strongly (fine for tests/one-offs).
    /// </summary>
    public static DojoRunSummary Summarize(string filePath, RunHistory run, Func<RunHistory>? runSource = null)
    {
        RunHistoryPlayer player = run.Players[0];

        int endHp = 0;
        int endMaxHp = 0;
        MapPointHistoryEntry? lastFloor = run.MapPointHistory.LastOrDefault()?.LastOrDefault();
        PlayerMapPointHistoryEntry? lastStats =
            lastFloor?.PlayerStats.FirstOrDefault(s => s.PlayerId == player.Id);
        if (lastStats != null)
        {
            endHp = lastStats.CurrentHp;
            endMaxHp = lastStats.MaxHp;
        }

        var summary = new DojoRunSummary
        {
            FilePath = filePath,
            CharacterId = player.Character,
            Ascension = run.Ascension,
            Win = run.Win,
            WasAbandoned = run.WasAbandoned,
            FloorsReached = run.MapPointHistory.Sum(act => act.Count),
            EndHp = endHp,
            EndMaxHp = endMaxHp,
            StartTime = run.StartTime,
            RunTimeSeconds = run.RunTime,
            Seed = run.Seed,
            DeckCount = player.Deck.Count(),
            RelicCount = player.Relics.Count(),
            Acts = ExtractActs(run),
            RunSource = runSource ?? (() => run)
        };
        summary.SeedRunCache(run);
        return summary;
    }

    /// <summary>
    /// Per-act boss/elite fights plus the final normal fight when a run dies there. Combat kind is keyed
    /// off each room's <c>room_type</c> (NOT <c>map_point_type</c> — CLAUDE.md §6: node type is misleading;
    /// a boss/elite room inside an event node is still that room type). Global floor numbering is 1-based
    /// across acts, matching RunHistoryQueries.FindCombatFloor and floor_added_to_deck. An ascension-10
    /// win naturally yields two bosses in the final act — two boss-room floors in map_point_history —
    /// with no special casing here.
    /// </summary>
    public static IReadOnlyList<DojoActSummary> ExtractActs(RunHistory run)
    {
        int totalFloors = run.MapPointHistory.Sum(act => act.Count);
        bool diedInCombat = !run.Win && !run.WasAbandoned;

        var acts = new List<DojoActSummary>(run.MapPointHistory.Count);
        int globalFloor = 0;
        for (int actIndex = 0; actIndex < run.MapPointHistory.Count; actIndex++)
        {
            var bosses = new List<DojoFightSummary>();
            var elites = new List<DojoFightSummary>();
            var otherDeathFights = new List<DojoFightSummary>();
            foreach (MapPointHistoryEntry floor in run.MapPointHistory[actIndex])
            {
                globalFloor++;
                foreach (MapPointRoomHistoryEntry room in floor.Rooms)
                {
                    if (room.ModelId == null)
                    {
                        continue;
                    }

                    bool isBoss = room.RoomType == RoomType.Boss;
                    bool isElite = room.RoomType == RoomType.Elite;
                    bool wasDeathFight = diedInCombat
                        && globalFloor == totalFloors
                        && Equals(room.ModelId, run.KilledByEncounter);
                    bool isOtherDeathFight = wasDeathFight && room.RoomType == RoomType.Monster;
                    if (!isBoss && !isElite && !isOtherDeathFight)
                    {
                        continue;
                    }

                    // The death fight: only the last visited floor of a lost, non-abandoned run can be
                    // it, and killed_by_encounter must actually match this room.
                    ModelId displayId = isOtherDeathFight && room.MonsterIds.Count == 1
                        ? room.MonsterIds[0]
                        : room.ModelId;
                    var fight = new DojoFightSummary(
                        globalFloor, room.ModelId, room.RoomType, displayId, wasDeathFight);
                    if (isBoss)
                    {
                        bosses.Add(fight);
                    }
                    else if (isElite)
                    {
                        elites.Add(fight);
                    }
                    else
                    {
                        otherDeathFights.Add(fight);
                    }
                }
            }

            ModelId? actId = actIndex < run.Acts.Count ? run.Acts[actIndex] : null;
            acts.Add(new DojoActSummary(actIndex, actId, bosses, elites, otherDeathFights));
        }

        return acts;
    }
}
