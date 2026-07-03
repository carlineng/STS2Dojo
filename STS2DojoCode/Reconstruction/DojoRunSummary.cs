using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

/// <summary>
/// One boss/elite fight in a run, as shown on a Dojo run-browser row's per-act strip. Pure data — the
/// display name and live content-eligibility of the encounter are resolved by the UI layer at row-build
/// time (see DojoFloorEligibility), not here, so this stays unit-testable against DTO test doubles.
/// <c>WasDeathFight</c> is true for the fight the player died in (lost, non-abandoned runs only): the
/// run's last visited floor whose combat room matches killed_by_encounter.
/// </summary>
public sealed record DojoFightSummary(
    int GlobalFloor,
    ModelId EncounterId,
    bool IsBoss,
    bool WasDeathFight);

/// <summary>Per-act slice of a run's boss/elite fights. ActId comes from the run file's top-level
/// acts list and can be null for acts beyond that list's length (defensive; not seen in the corpus).</summary>
public sealed record DojoActSummary(
    int ActIndex,
    ModelId? ActId,
    IReadOnlyList<DojoFightSummary> Bosses,
    IReadOnlyList<DojoFightSummary> Elites);

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

    /// <summary>The full parsed run, kept so a pill/row click can hand it straight to
    /// DojoReplayLauncher / the stock NRunHistory without re-reading the file.</summary>
    public required RunHistory Run { get; init; }
}

public static class DojoRunSummarizer
{
    /// <summary>
    /// Builds the row summary for a single-player run. Caller is responsible for having already gated
    /// the run through <see cref="RunHistoryFileSelector"/> (multiplayer/modifier/no-combat exclusions) —
    /// this only distills display data and assumes players[0] is the (sole) player.
    /// </summary>
    public static DojoRunSummary Summarize(string filePath, RunHistory run)
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

        return new DojoRunSummary
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
            Run = run
        };
    }

    /// <summary>
    /// Per-act boss/elite fights, keyed off each room's <c>room_type</c> (NOT <c>map_point_type</c> —
    /// CLAUDE.md §6: node type is misleading; a boss/elite room inside an event node is still that room
    /// type). Global floor numbering is 1-based across acts, matching RunHistoryQueries.FindCombatFloor
    /// and floor_added_to_deck. An ascension-10 win naturally yields two bosses in the final act — two
    /// boss-room floors in map_point_history — with no special casing here.
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
            foreach (MapPointHistoryEntry floor in run.MapPointHistory[actIndex])
            {
                globalFloor++;
                foreach (MapPointRoomHistoryEntry room in floor.Rooms)
                {
                    bool isBoss = room.RoomType == RoomType.Boss;
                    bool isElite = room.RoomType == RoomType.Elite;
                    if ((!isBoss && !isElite) || room.ModelId == null)
                    {
                        continue;
                    }

                    // The death fight: only the last visited floor of a lost, non-abandoned run can be
                    // it, and killed_by_encounter must actually match this room (a death to an event or
                    // to a plain monster room on that floor must not tag a boss/elite pill as fatal).
                    bool wasDeathFight = diedInCombat
                        && globalFloor == totalFloors
                        && Equals(room.ModelId, run.KilledByEncounter);

                    (isBoss ? bosses : elites).Add(
                        new DojoFightSummary(globalFloor, room.ModelId, isBoss, wasDeathFight));
                }
            }

            ModelId? actId = actIndex < run.Acts.Count ? run.Acts[actIndex] : null;
            acts.Add(new DojoActSummary(actIndex, actId, bosses, elites));
        }

        return acts;
    }
}
