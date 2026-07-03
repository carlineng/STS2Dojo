using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using STS2Dojo.STS2DojoCode.Reconstruction;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Whether a floor can be offered for Dojo replay at all - used both to gate the click handler
/// (<c>DojoRunHistoryFloorClickPatch.cs</c>) and to grey out ineligible floor icons in the run browser
/// before the player ever clicks (CLAUDE.md §9 item 5).
///
/// The content-resolve half deliberately does NOT reuse <c>DojoLaunch</c>/<c>DojoReplayLauncher</c>'s full
/// launch sequence (<c>RunManager.SetUpNewSingleplayer</c>, asset preload, scene creation) - none of that
/// is needed just to know whether a loadout's content ids resolve, and running it once per floor icon (up
/// to 48 per run) would be far too slow and would have real side effects (RunManager.State, asset loads).
/// Instead it builds a throwaway <see cref="RunState"/>/<see cref="Player"/> entirely in memory -
/// <c>RunState.CreateForNewRun</c> sets <c>player.RunState</c> directly with no <c>RunManager</c>
/// involvement - purely to get an ascension-adjusted starting deck/relics snapshot (the same "true starting
/// inventory" problem as CLAUDE.md §5a point 3), then runs the same <c>RunReconstructor</c> +
/// <c>DojoContentEligibility</c> pipeline <c>DojoReplayLauncher</c> uses for a real launch. The throwaway
/// RunState/Player is never registered with <c>RunManager</c> and is simply discarded (garbage collected)
/// once this returns.
/// </summary>
public static class DojoFloorEligibility
{
    private sealed record StartingSnapshot(
        List<SerializableCard> Deck,
        List<SerializableRelic> Relics,
        int MaxHp,
        int Gold);

    /// <summary>The throwaway-player starting snapshot depends only on (character, ascension), and
    /// building it (Player.CreateForNewRun + RunState.CreateForNewRun + AscensionManager) is by far the
    /// expensive part of an eligibility check — the reconstruction replay itself is just list walking.
    /// Cached so per-floor checks (48 icons in the stock run-history view, up to ~13 pills per custom
    /// Dojo-screen row) each cost microseconds instead of a fresh player build. The snapshot's
    /// serializable DTOs are only ever read downstream (RunReconstructor is pure data-in/data-out), so
    /// sharing the instances across checks is safe.</summary>
    private static readonly Dictionary<(ModelId Character, int Ascension), StartingSnapshot> SnapshotCache = new();

    /// <summary>Whether this floor contains a combat room at all (monster/elite/boss) — the only floors
    /// that are ever replay candidates. Used to distinguish a non-combat floor (rest/shop/event/treasure,
    /// left rendered normally in the in-row map — it was never a replay target) from an ineligible combat
    /// floor (greyed out, because its content no longer resolves). Deliberately independent of the
    /// single-player/modifier gates in <see cref="IsStructurallyReplayable"/>: those exclude whole runs
    /// upstream (they never render), so here the only question is "is this a fight."</summary>
    public static bool IsCombatFloor(MapPointHistoryEntry entry) =>
        entry.Rooms.Any(RunHistoryQueries.IsCombatRoom);

    /// <summary>Cheap, non-reconstruction structural checks (CLAUDE.md §6/§10): single-player only, no
    /// modifier-bearing runs, and only floors with an actual combat room.</summary>
    public static bool IsStructurallyReplayable(RunHistory history, MapPointHistoryEntry entry) =>
        RunHistoryQueries.IsSinglePlayer(history)
        && history.Modifiers.Count == 0
        && entry.Rooms.Any(RunHistoryQueries.IsCombatRoom);

    /// <summary>Full eligibility for a specific floor: the structural checks above, plus a live
    /// content-resolve pass (CLAUDE.md §6/§5f) against a cheap, throwaway reconstruction preview. Never
    /// throws - any failure (bad/unexpected data, unresolvable content, a reconstruction error) means "not
    /// eligible," matching <see cref="DojoContentEligibilityException"/>'s own refuse-don't-crash
    /// philosophy.</summary>
    public static bool IsEligible(RunHistory history, MapPointHistoryEntry entry, int globalFloor)
    {
        if (!IsStructurallyReplayable(history, entry))
        {
            return false;
        }

        try
        {
            StartingSnapshot snapshot = GetStartingSnapshot(history.Players.Single().Character, history.Ascension);

            ReconstructedLoadout loadout = RunReconstructor.Reconstruct(
                history, globalFloor, snapshot.Deck, snapshot.Relics, snapshot.MaxHp, snapshot.Gold);

            return DojoContentEligibility.Validate(loadout, LiveDojoContentResolver.Instance).IsEligible;
        }
        catch
        {
            return false;
        }
    }

    private static StartingSnapshot GetStartingSnapshot(ModelId characterId, int ascension)
    {
        (ModelId, int) key = (characterId, ascension);
        if (SnapshotCache.TryGetValue(key, out StartingSnapshot? cached))
        {
            return cached;
        }

        CharacterModel character = ModelDb.GetById<CharacterModel>(characterId);
        Player previewPlayer = Player.CreateForNewRun(character, UnlockState.all, netId: 1uL);
        RunState.CreateForNewRun(
            new List<Player> { previewPlayer },
            ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(),
            Array.Empty<ModifierModel>(),
            GameMode.Standard,
            ascension,
            SeedHelper.GetRandomSeed());
        new AscensionManager(ascension).ApplyEffectsTo(previewPlayer);

        var snapshot = new StartingSnapshot(
            previewPlayer.Deck.Cards.Select(c => c.ToSerializable()).ToList(),
            previewPlayer.Relics.Select(r => r.ToSerializable()).ToList(),
            previewPlayer.Creature.MaxHp,
            previewPlayer.Gold);
        SnapshotCache[key] = snapshot;
        return snapshot;
    }
}
