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
            CharacterModel character = ModelDb.GetById<CharacterModel>(history.Players.Single().Character);
            Player previewPlayer = Player.CreateForNewRun(character, UnlockState.all, netId: 1uL);
            RunState.CreateForNewRun(
                new List<Player> { previewPlayer },
                ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(),
                Array.Empty<ModifierModel>(),
                GameMode.Standard,
                history.Ascension,
                SeedHelper.GetRandomSeed());
            new AscensionManager(history.Ascension).ApplyEffectsTo(previewPlayer);

            List<SerializableCard> startingDeck = previewPlayer.Deck.Cards.Select(c => c.ToSerializable()).ToList();
            List<SerializableRelic> startingRelics = previewPlayer.Relics.Select(r => r.ToSerializable()).ToList();

            ReconstructedLoadout loadout = RunReconstructor.Reconstruct(
                history, globalFloor, startingDeck, startingRelics, previewPlayer.Creature.MaxHp, previewPlayer.Gold);

            return DojoContentEligibility.Validate(loadout, LiveDojoContentResolver.Instance).IsEligible;
        }
        catch
        {
            return false;
        }
    }
}
