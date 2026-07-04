using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Replaces a throwaway Dojo player's auto-populated starting inventory with a target loadout, inside a
/// <see cref="DojoLaunch"/> mutate callback (i.e. BEFORE the run's scene exists — see DojoLaunch's class
/// docs for why that ordering is load-bearing). Extracted from <c>DojoReplayLauncher</c> (2026-07-04) so
/// the §12 shared-fight import path applies a payload through the exact same subtle sequence instead of
/// a drift-prone copy; every comment below documents a bug that was actually hit (CLAUDE.md §5a).
/// </summary>
public static class DojoLoadoutApplier
{
    public static void Apply(
        RunState runState,
        Player player,
        IReadOnlyList<SerializableCard> deck,
        IReadOnlyList<SerializableRelic> relics,
        IReadOnlyList<SerializablePotion> potions,
        int maxPotionSlots,
        int gold,
        int maxHp,
        int currentHp)
    {
        // Cards use silent:true + one InvokeCardAddFinished() flush at the end (CardPile's intended
        // pattern for a bulk rebuild — see NTopBarDeckButton, which only refreshes on CardAddFinished/
        // CardRemoveFinished, not the per-card CardAdded event). Relics/potions have no such batching
        // hook, but silent:true is still passed for consistency — no UI exists to receive
        // RelicObtained/RelicRemoved/PotionUsed events yet at this point in the sequence (the run's
        // scene isn't created until after the mutate callback returns), so it's a no-op today, just
        // future-proofing against that changing.
        player.Deck.Clear(silent: true);
        foreach (RelicModel relic in player.Relics.ToList())
        {
            player.RemoveRelicInternal(relic, silent: true);
        }
        foreach (PotionModel potion in player.Potions.ToList())
        {
            player.DiscardPotionInternal(potion, silent: true);
        }

        foreach (SerializableCard card in deck)
        {
            // Must go through RunState.LoadCard (not CardModel.FromSerializable directly) — it's what
            // sets CardModel.Owner and registers the card with the run. Skipping it leaves Owner null,
            // which NREs the first time the hook system walks the deck (RunState.Contains).
            CardModel loaded = runState.LoadCard(card, player);
            player.Deck.AddInternal(loaded, index: -1, silent: true);
        }
        player.Deck.InvokeCardAddFinished();

        foreach (SerializableRelic relic in relics)
        {
            // Deliberately NOT calling RelicModel.AfterObtained() here. AfterObtained() applies a
            // relic's one-time PICKUP effect (e.g. Pear/Mango permanent Max HP boosts,
            // Whetstone/GnarledHammer one-time card upgrades, and several relics that pop an
            // interactive card-selection/reward screen). Every relic here was already picked up in the
            // run it came from, and its effect is already baked into the loadout's HP/gold/card values —
            // calling AfterObtained() again would double-apply stat effects and pop inappropriate UI
            // prompts mid-launch. This is intentional, not a missed call.
            player.AddRelicInternal(RelicModel.FromSerializable(relic), index: -1, silent: true);
        }

        // Reconcile potion slot count to the loadout's true value (ascension's Tight Belt baseline +
        // any Potion Belt/Alchemical Coffer/Phial Holster in the loadout). The throwaway player already
        // gets the ascension reduction automatically (RunManager's launch sequence applies
        // AscensionManager.ApplyEffectsTo for every new player), but NOT the relic-based bonuses, since
        // applied relics deliberately skip AfterObtained() (see the relic loop above) — so this
        // reconciles explicitly rather than assuming either side is already correct. Do NOT just grow
        // to fit potions.Count: that would silently paper over an upstream bug if the potion count and
        // the true slot count ever disagree, instead of surfacing it.
        int slotDelta = maxPotionSlots - player.MaxPotionCount;
        if (slotDelta > 0)
        {
            player.AddToMaxPotionCount(slotDelta);
        }
        else if (slotDelta < 0)
        {
            player.SubtractFromMaxPotionCount(-slotDelta);
        }

        foreach (SerializablePotion potion in potions)
        {
            // slotIndex -1 (first free slot): list order preserves the captured slot order, and forcing
            // exact captured indexes could collide with a reconciled-smaller slot count.
            player.AddPotionInternal(PotionModel.FromSerializable(potion), slotIndex: -1, silent: true);
        }

        player.Gold = gold;
        player.Creature.SetMaxHpInternal(maxHp);
        player.Creature.SetCurrentHpInternal(currentHp);
    }
}
