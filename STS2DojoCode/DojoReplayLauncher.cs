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

    /// <summary>Convenience overload for callers (e.g. the run browser's floor click) that haven't already
    /// resolved an encounter id — resolves it internally and folds any resolution failure into the same
    /// log-and-swallow behavior as a launch failure, since there's no synchronous caller waiting on a result.</summary>
    public static async Task LaunchReplay(RunHistory run, int globalFloor)
    {
        try
        {
            ModelId encounterId = ResolveEncounterId(run, globalFloor);
            await LaunchReplay(run, globalFloor, encounterId);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Dojo replay launch failed: " + e);
        }
    }

    public static async Task LaunchReplay(RunHistory run, int globalFloor, ModelId encounterId)
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

                // Replace the auto-populated starting inventory with the reconstructed one. Cards use
                // silent:true + one InvokeCardAddFinished() flush at the end (CardPile's intended pattern
                // for a bulk rebuild — see NTopBarDeckButton, which only refreshes on CardAddFinished/
                // CardRemoveFinished, not the per-card CardAdded event). Relics/potions have no such
                // batching hook, but silent:true is still passed for consistency — no UI exists to
                // receive RelicObtained/RelicRemoved/PotionUsed events yet at this point in the sequence
                // (the run's scene isn't created until after this callback returns), so it's a no-op today,
                // just future-proofing against that changing.
                player.Deck.Clear(silent: true);
                foreach (RelicModel relic in player.Relics.ToList())
                {
                    player.RemoveRelicInternal(relic, silent: true);
                }
                foreach (PotionModel potion in player.Potions.ToList())
                {
                    player.DiscardPotionInternal(potion, silent: true);
                }

                foreach (ProvenancedCard pc in loadout.Deck)
                {
                    // Must go through RunState.LoadCard (not CardModel.FromSerializable directly) — it's
                    // what sets CardModel.Owner and registers the card with the run. Skipping it leaves
                    // Owner null, which NREs the first time the hook system walks the deck
                    // (RunState.Contains).
                    CardModel card = runState.LoadCard(pc.Card, player);
                    player.Deck.AddInternal(card, index: -1, silent: true);
                }
                player.Deck.InvokeCardAddFinished();

                foreach (ProvenancedRelic pr in loadout.Relics)
                {
                    // Deliberately NOT calling RelicModel.AfterObtained() here. AfterObtained() applies a
                    // relic's one-time PICKUP effect (e.g. Pear/Mango permanent Max HP boosts,
                    // Whetstone/GnarledHammer one-time card upgrades, and several relics that pop an
                    // interactive card-selection/reward screen). Every reconstructed relic here was
                    // already picked up earlier in the ORIGINAL run, and its effect is already baked into
                    // the run file's logged HP/gold/card-upgrade values that RunReconstructor reads
                    // directly — calling AfterObtained() again would double-apply stat effects and pop
                    // inappropriate UI prompts mid-launch. This is intentional, not a missed call.
                    player.AddRelicInternal(RelicModel.FromSerializable(pr.Relic), index: -1, silent: true);
                }

                // Reconcile potion slot count to loadout.MaxPotionSlots (ascension's Tight Belt baseline +
                // any Potion Belt/Alchemical Coffer/Phial Holster picked up — see RunReconstructor). The
                // throwaway player already gets the ascension reduction automatically (RunManager's launch
                // sequence applies AscensionManager.ApplyEffectsTo for every new player), but NOT the
                // relic-based bonuses, since reconstructed relics deliberately skip AfterObtained() (see
                // the relic loop above) — so this reconciles explicitly rather than assuming either side
                // is already correct. Do NOT just grow to fit loadout.Potions.Count: that would silently
                // paper over a reconstruction bug if the potion count and the true slot count ever
                // disagree, instead of surfacing it.
                int slotDelta = loadout.MaxPotionSlots - player.MaxPotionCount;
                if (slotDelta > 0)
                {
                    player.AddToMaxPotionCount(slotDelta);
                }
                else if (slotDelta < 0)
                {
                    player.SubtractFromMaxPotionCount(-slotDelta);
                }

                foreach (ProvenancedPotion pp in loadout.Potions)
                {
                    player.AddPotionInternal(PotionModel.FromSerializable(new SerializablePotion { Id = pp.PotionId }),
                        slotIndex: -1, silent: true);
                }

                player.Gold = loadout.Gold;
                player.Creature.SetMaxHpInternal(loadout.MaxHp);
                player.Creature.SetCurrentHpInternal(loadout.CurrentHp);

                MainFile.Logger.Info(
                    $"[STS2Dojo] Replay launch: '{encounterId.Entry}' character={character.Id.Entry} " +
                    $"deck={loadout.Deck.Count} relics={loadout.Relics.Count} hp={loadout.CurrentHp}/{loadout.MaxHp} " +
                    $"gold={loadout.Gold} ascension={loadout.Ascension}.");
            });
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Dojo replay launch failed: " + e);
        }
    }
}
