using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Dojo.STS2DojoCode.Reconstruction;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// The reconstruction+launch pipeline shared by the <c>dojoreplay</c> console command
/// (<see cref="DojoReplayConsoleCmd"/>) and the Dojo run browser's floor-click handler
/// (<c>DojoRunHistoryFloorClickPatch.cs</c>, CLAUDE.md §9 roadmap item 5). Both already have an in-memory
/// <see cref="RunHistory"/> (loaded from a file path or from <c>SaveManager</c> via the run browser) and a
/// target floor — everything past that point (resolving the combat encounter, snapshotting the true starting
/// inventory, running <see cref="RunReconstructor"/>, and launching via <see cref="DojoLaunch"/>) is identical
/// regardless of caller.
/// </summary>
public static class DojoReplayLauncher
{
    /// <summary>Resolves and validates the encounter for a run's combat floor, without launching anything.
    /// Throws <see cref="MegaCrit.Sts2.Core.Models.Exceptions.ModelNotFoundException"/> if the encounter no
    /// longer resolves against the currently-loaded content (e.g. game content changed since this run), or
    /// <see cref="InvalidOperationException"/>/<see cref="ArgumentOutOfRangeException"/> if the floor has no
    /// combat room. Exposed separately from <see cref="LaunchReplay(RunHistory,int,ModelId)"/> so a caller
    /// (e.g. a console command) can give the player an immediate, synchronous error message for a bad
    /// floor/encounter instead of only finding out after an async launch has already started.</summary>
    public static ModelId ResolveEncounterId(RunHistory run, int globalFloor)
    {
        (_, MapPointRoomHistoryEntry combatRoom) = RunReconstructor.FindCombatFloor(run, globalFloor);
        ModelId encounterId = combatRoom.ModelId
            ?? throw new InvalidOperationException($"Floor {globalFloor}'s combat room is missing model_id.");
        ModelDb.GetById<EncounterModel>(encounterId); // validates existence; throws ModelNotFoundException if missing
        return encounterId;
    }

    /// <summary>Convenience overload for callers (e.g. the Replay Setup modal) that haven't already
    /// resolved an encounter id — resolves it internally and folds any resolution failure into the same
    /// log-and-swallow behavior as a launch failure, since there's no synchronous caller waiting on a result.</summary>
    public static async Task LaunchReplay(RunHistory run, int globalFloor, DojoStateAdjustments? adjustments = null)
    {
        try
        {
            ModelId encounterId = ResolveEncounterId(run, globalFloor);
            await LaunchReplay(run, globalFloor, encounterId, adjustments);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Dojo replay launch failed: " + e);
        }
    }

    public static async Task LaunchReplay(
        RunHistory run, int globalFloor, ModelId encounterId, DojoStateAdjustments? adjustments = null)
    {
        try
        {
            NGame? game = NGame.Instance;
            if (game == null)
            {
                MainFile.Logger.Error("[STS2Dojo] NGame.Instance is null; cannot launch Dojo replay.");
                return;
            }

            CharacterModel character = ModelDb.GetById<CharacterModel>(run.Players.Single().Character);

            // The mutate callback runs before the run's scene is created (see DojoLaunch.cs's class docs)
            // — this is what fixes the previously-invisible relic icons (CLAUDE.md §5a point 6).
            await DojoLaunch.LaunchThrowawayRun(game, character, run.Ascension, encounterId, mutate: runState =>
            {
                Player player = runState.Players[0];

                // Snapshot the TRUE ascension-adjusted starting inventory (e.g. Ascender's Bane at high
                // ascension) that LaunchThrowawayRun just auto-populated, before we replace it.
                List<SerializableCard> startingDeck = player.Deck.Cards.Select(c => c.ToSerializable()).ToList();
                List<SerializableRelic> startingRelics = player.Relics.Select(r => r.ToSerializable()).ToList();
                int startingHp = player.Creature.MaxHp;
                int startingGold = player.Gold;

                ReconstructedLoadout loadout = RunReconstructor.Reconstruct(
                    run, globalFloor, startingDeck, startingRelics, startingHp, startingGold);

                // Refuse the fight if it depends on content that no longer resolves (renamed/removed
                // encounter/card/relic since this run was played, or content from an uninstalled mod - see
                // CLAUDE.md §6). Thrown before any player mutation below, so DojoLaunch's existing
                // mutate-failure recovery (RunManager.CleanUp(), no scene ever created) handles teardown.
                DojoContentEligibilityResult eligibility =
                    DojoContentEligibility.Validate(loadout, LiveDojoContentResolver.Instance);
                if (!eligibility.IsEligible)
                {
                    throw new DojoContentEligibilityException(eligibility.MissingContent);
                }

                // Player-tuned relic/card counter state from the Replay Setup modal. Stamped onto the
                // freshly-reconstructed serializable DTOs (per-launch instances, safe to mutate) so the
                // values restore through the game's own save pipeline: FromSerializable below calls
                // SavedProperties.Fill, the same way a mid-run save reload restores relic counters.
                adjustments?.ApplyTo(loadout);

                // Replace the auto-populated starting inventory with the reconstructed one, via the
                // shared applier (also used by the §12 shared-fight import path) — the subtle
                // LoadCard/silent/slot-reconcile sequence and its rationale live there.
                DojoLoadoutApplier.Apply(
                    runState,
                    player,
                    loadout.Deck.Select(pc => pc.Card).ToList(),
                    loadout.Relics.Select(pr => pr.Relic).ToList(),
                    loadout.Potions.Select(pp => new SerializablePotion { Id = pp.PotionId }).ToList(),
                    loadout.MaxPotionSlots,
                    loadout.Gold,
                    loadout.MaxHp,
                    loadout.CurrentHp);

                MainFile.Logger.Info(
                    $"[STS2Dojo] Replay launch: '{encounterId.Entry}' character={character.Id.Entry} " +
                    $"deck={loadout.Deck.Count} relics={loadout.Relics.Count} hp={loadout.CurrentHp}/{loadout.MaxHp} " +
                    $"gold={loadout.Gold} ascension={loadout.Ascension} stateAdjustments={adjustments?.Count ?? 0}.");
            });
        }
        catch (DojoContentEligibilityException e)
        {
            // Expected/user-facing refusal, not a bug - log just the message, no stack trace.
            MainFile.Logger.Error("[STS2Dojo] " + e.Message);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Dojo replay launch failed: " + e);
        }
    }
}
