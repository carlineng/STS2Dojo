using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

/// <summary>
/// Turns a <see cref="RunHistory"/> + a global 1-based floor index into the player state entering the
/// fight on that floor (CLAUDE.md §5's reconstruction contract). Forward-replays deck/relic deltas from
/// floor 1 up to (but NOT including) the target floor: a combat floor's own player_stats entry bundles
/// that floor's fight rewards (card/relic choices), so including floor N's own deltas would hand the
/// player rewards from a fight they haven't (re)played yet — see the walkthrough that motivated this in
/// the STS2 Dojo session that added this file.
/// </summary>
public static class RunReconstructor
{
    /// <summary>Locates the target floor and its combat room. Cheap and side-effect-free — callers use
    /// this to resolve/validate the encounter before doing the (heavier, live-game-dependent) full
    /// reconstruction.</summary>
    public static (MapPointHistoryEntry Floor, MapPointRoomHistoryEntry CombatRoom) FindCombatFloor(
        RunHistory run, int globalFloor) =>
        RunHistoryQueries.FindCombatFloor(run, globalFloor);

    /// <summary>
    /// Reconstructs the loadout entering the fight on <paramref name="globalFloor"/>. The starting
    /// deck/relics/HP/gold are supplied by the caller rather than looked up from
    /// <c>CharacterModel</c> here, because the *true* ascension-adjusted starting inventory (e.g. Silent's
    /// Ascender's Bane curse at high ascension) is only known once the game has actually created a player
    /// for that character+ascension — <c>CharacterModel.StartingDeck</c> alone doesn't include it.
    /// </summary>
    /// <summary>Base potion slot count before any ascension/relic adjustment (Player.initialMaxPotionSlotCount).</summary>
    private const int BasePotionSlots = 3;

    /// <summary>Ascension level at which "Tight Belt" kicks in (AscensionLevel.TightBelt = 4), permanently
    /// reducing max potion slots by 1 for the whole run (AscensionManager.ApplyEffectsTo).</summary>
    private const int TightBeltAscension = 4;

    /// <summary>Relics that grant a one-time, permanent max-potion-slot increase on pickup
    /// (RelicModel.AfterObtained -> PlayerCmd.GainMaxPotionCount), keyed by the exact bonus each grants.</summary>
    private static readonly Dictionary<string, int> PotionSlotGrantingRelics = new()
    {
        ["RELIC.POTION_BELT"] = 2,
        ["RELIC.ALCHEMICAL_COFFER"] = 4,
        ["RELIC.PHIAL_HOLSTER"] = 1
    };

    /// <summary>
    /// Cards whose [SavedProperty] state is immutable *identity* chosen at acquisition — NOT a per-combat
    /// counter. The per-floor deltas the forward-replay walks (cards_gained) carry only a bare id, and
    /// StampFloor deliberately drops Props, so without a backfill these cards are rebuilt at their type
    /// default. For Mad Science that default is CardType.None: the card renders as "?????" and its OnPlay
    /// switch hits `default: throw`, freezing the card mid-play (the bug this set fixes). Its TinkerTime
    /// type/rider are set once at the Tinker Time event and never change, so the end-of-run deck snapshot's
    /// value is exactly the value the card had entering any earlier fight — safe to copy back verbatim.
    ///
    /// Deliberately excludes the four *scaling* stateful cards (Genetic Algorithm, The Scythe, Guilty,
    /// Spoils Map): their counters legitimately grow over the run, so the end-of-run value is wrong for an
    /// earlier floor, AND their type default is already functional (no crash). The scaling ones are instead
    /// exposed as editable `assumed` state in the Replay Setup modal (§5n). This is an id allowlist, not a
    /// blanket "copy all final props," precisely to keep those four off it.
    /// </summary>
    private static readonly HashSet<string> IdentityPropCardIds = new() { "CARD.MAD_SCIENCE" };

    public static ReconstructedLoadout Reconstruct(
        RunHistory run,
        int globalFloor,
        IReadOnlyList<SerializableCard> startingDeck,
        IReadOnlyList<SerializableRelic> startingRelics,
        int startingHp,
        int startingGold,
        ulong playerId = 1)
    {
        if (!RunHistoryQueries.IsSinglePlayer(run, playerId))
        {
            throw new InvalidOperationException(
                $"Only single-player runs are supported (CLAUDE.md §6); expected exactly one player with id {playerId}.");
        }

        RunHistoryPlayer finalPlayer = run.Players.Single();
        IReadOnlyList<MapPointHistoryEntry> floors = RunHistoryQueries.FlattenFloors(run);
        (_, MapPointRoomHistoryEntry combatRoom) = FindCombatFloor(run, globalFloor);
        ModelId encounterId = combatRoom.ModelId
            ?? throw new InvalidOperationException($"Floor {globalFloor}'s combat room is missing model_id.");

        List<ProvenancedCard> deck = startingDeck
            .Select(c => new ProvenancedCard(c, Provenance.Derived))
            .ToList();
        List<ProvenancedRelic> relics = startingRelics
            .Select(r => new ProvenancedRelic(r, Provenance.Derived))
            .ToList();
        List<ProvenancedPotion> potions = [];
        int maxPotionSlots = BasePotionSlots - (run.Ascension >= TightBeltAscension ? 1 : 0);

        int currentHp = startingHp;
        int maxHp = startingHp;
        int gold = startingGold;

        for (int floorIdx = 1; floorIdx < globalFloor; floorIdx++)
        {
            PlayerMapPointHistoryEntry? ps = floors[floorIdx - 1].PlayerStats
                .FirstOrDefault(p => p.PlayerId == playerId);
            if (ps == null)
            {
                continue;
            }

            currentHp = ps.CurrentHp;
            maxHp = ps.MaxHp;
            gold = ps.CurrentGold;

            // NOTE: card_choices (picked) and bought_colorless are deliberately NOT applied here — a
            // corpus scan of all 531 single-player files / 13,754 floors in runfiles/ found every picked
            // card_choices entry and every bought_colorless entry already duplicated in cards_gained for
            // the same floor (0 counterexamples). Applying both double-counts the card.
            foreach (SerializableCard gained in ps.CardsGained)
            {
                deck.Add(new ProvenancedCard(StampFloor(gained, floorIdx), Provenance.Replayed));
            }

            foreach (SerializableCard removed in ps.CardsRemoved)
            {
                RemoveOneMatchingCard(deck, removed);
            }

            foreach (CardTransformationHistoryEntry transformed in ps.CardsTransformed)
            {
                RemoveOneMatchingCard(deck, transformed.OriginalCard);
                deck.Add(new ProvenancedCard(StampFloor(transformed.FinalCard, floorIdx), Provenance.Replayed));
            }

            foreach (CardEnchantmentHistoryEntry enchanted in ps.CardsEnchanted)
            {
                ApplyEnchant(deck, enchanted);
            }

            foreach (ModelId upgradedId in ps.UpgradedCards)
            {
                ApplyUpgradeDelta(deck, upgradedId, +1);
            }

            foreach (ModelId downgradedId in ps.DowngradedCards)
            {
                ApplyUpgradeDelta(deck, downgradedId, -1);
            }

            // bought_relics is likewise NOT applied — same corpus scan found every bought_relics entry
            // already duplicated by a relic_choices(picked) entry on the same floor (0 counterexamples;
            // 378 floors had the overlap). relic_choices(picked) is the authoritative source.
            foreach (ModelChoiceHistoryEntry choice in ps.RelicChoices)
            {
                if (choice.wasPicked)
                {
                    relics.Add(new ProvenancedRelic(
                        new SerializableRelic { Id = choice.choice, FloorAddedToDeck = floorIdx }, Provenance.Replayed));

                    // Potion Belt/Alchemical Coffer/Phial Holster grant a one-time permanent slot increase
                    // via AfterObtained() on pickup, which the reconstructor deliberately never calls (see
                    // the "Deliberately NOT calling RelicModel.AfterObtained()" note at the launch site) —
                    // their effect on HP/gold/deck is already baked into the run's logged values, but a
                    // *slot count* has no such log to read back from, so it's tracked here instead.
                    if (choice.choice != null &&
                        PotionSlotGrantingRelics.TryGetValue(choice.choice.ToString(), out int slotBonus))
                    {
                        maxPotionSlots += slotBonus;
                    }
                }
            }

            foreach (ModelId removedId in ps.RelicsRemoved)
            {
                RemoveOneMatchingRelic(relics, removedId);
            }

            // Potions. potion_choices(picked) is the sole authoritative gain source: bought_potions is NOT
            // applied — a corpus scan of all 531 single-player files found every one of 216 bought_potions
            // entries already duplicated by a picked potion_choices entry on the same floor (0
            // counterexamples), the same redundancy pattern as bought_relics/card_choices above.
            // potion_used/potion_discarded are removals; both are logged by every call that goes through
            // PotionCmd.Discard / PotionModel.OnUseWrapper.
            //
            // Event-driven potion grants/removals (Drowning Beacon, Potion Courier, Ranwid the Elder,
            // Stone of All Time) were investigated as a possible additional source, since their
            // event_choices[].variables mention a potion by name. They turned out to need NO special
            // handling: every one of their potion-touching options (Drowning Beacon's Bottle, Potion
            // Courier's Grab Potions, Ranwid's Potion option, Stone of All Time's Lift) goes through
            // PotionCmd.TryToProcure/Discard just like any other potion gain/loss, so it's already fully
            // captured by potion_choices/potion_used/potion_discarded above — confirmed by cross-checking
            // every corpus occurrence against the *specific option chosen* (100% structurally covered
            // whenever a potion was actually touched). The `variables` dict is populated for ALL of an
            // event's possible options at generation time regardless of which one the player picks (it's
            // needed to render every option's hover text), so a naive check keyed on the event's name alone
            // — rather than the chosen option — produces false positives: e.g. Drowning Beacon's "Potion"
            // variable is present even when the player picks Climb (gains a relic, HP loss, no potion at
            // all) or Ranwid's "Potion" variable is present even when Gold or Relic was chosen. An earlier
            // version of this reconstructor keyed on event name only and generated exactly these
            // false-positive grants/removals (caught via an in-game potion-slot-count mismatch — see the
            // STS2 Dojo session that removed this). Potion Courier's Ransack option is the one genuine
            // gap: it grants ONE random uncommon-rarity potion via base.Rng — true combat-adjacent RNG,
            // unrecoverable from the log (same as combat RNG generally, CLAUDE.md §5) — so on the rare
            // occasion it isn't picked up by potion_choices (e.g. declined because slots were full), it's
            // correctly left unresolved rather than guessed.
            foreach (ModelChoiceHistoryEntry potionChoice in ps.PotionChoices)
            {
                if (potionChoice.wasPicked)
                {
                    potions.Add(new ProvenancedPotion(potionChoice.choice!, Provenance.Replayed));
                }
            }

            foreach (ModelId usedId in ps.PotionUsed)
            {
                RemoveOneMatchingPotion(potions, usedId);
            }

            foreach (ModelId discardedId in ps.PotionDiscarded)
            {
                RemoveOneMatchingPotion(potions, discardedId);
            }
        }

        RestoreIdentityProps(deck, finalPlayer.Deck);

        return new ReconstructedLoadout
        {
            CharacterId = finalPlayer.Character,
            Ascension = run.Ascension,
            CurrentHp = currentHp,
            MaxHp = maxHp,
            Gold = gold,
            Deck = deck,
            Relics = relics,
            EncounterId = encounterId,
            MonsterIds = combatRoom.MonsterIds.ToList(),
            Potions = potions,
            MaxPotionSlots = maxPotionSlots
        };
    }

    /// <summary>
    /// Copies a card, defaulting FloorAddedToDeck to the floor it was gained on when the source delta
    /// didn't carry one (common for cards_gained — see runfiles/SCHEMA.md). Deliberately drops Props:
    /// per-floor relic/card counter state isn't recoverable (CLAUDE.md §5), so a freshly-reconstructed
    /// card should start with none rather than carry stale counter noise from the log.
    /// </summary>
    private static SerializableCard StampFloor(SerializableCard card, int floorIdx) => new()
    {
        Id = card.Id,
        CurrentUpgradeLevel = card.CurrentUpgradeLevel,
        Enchantment = card.Enchantment,
        FloorAddedToDeck = card.FloorAddedToDeck ?? floorIdx
    };

    /// <summary>
    /// Backfills identity-prop cards (see <see cref="IdentityPropCardIds"/>) in the forward-replayed deck
    /// from the run's end-of-run deck snapshot (<c>players[0].deck</c>), the only place those props are
    /// logged. Matches a reconstructed instance to a snapshot instance by (id, floor_added_to_deck), falling
    /// back to id-only if the floor doesn't line up (e.g. a duplicated copy). Only fills a card whose Props
    /// are currently null so a deliberately-empty prop set is never clobbered, and only for allowlisted ids
    /// so scaling cards are untouched. A card that was removed before end-of-run won't appear in the snapshot
    /// and is left as-is (an accepted, rare gap — recovering it would require replaying the Tinker Time event).
    /// </summary>
    private static void RestoreIdentityProps(List<ProvenancedCard> deck, IEnumerable<SerializableCard> finalDeck)
    {
        List<SerializableCard> finalIdentityCards = finalDeck
            .Where(c => c.Id != null && c.Props != null && IdentityPropCardIds.Contains(c.Id.ToString()!))
            .ToList();
        if (finalIdentityCards.Count == 0)
        {
            return;
        }

        for (int i = 0; i < deck.Count; i++)
        {
            SerializableCard card = deck[i].Card;
            if (card.Id == null || card.Props != null || !IdentityPropCardIds.Contains(card.Id.ToString()!))
            {
                continue;
            }

            SerializableCard? match =
                finalIdentityCards.FirstOrDefault(fc => fc.Id == card.Id && fc.FloorAddedToDeck == card.FloorAddedToDeck)
                ?? finalIdentityCards.FirstOrDefault(fc => fc.Id == card.Id);
            if (match == null)
            {
                continue;
            }

            deck[i] = new ProvenancedCard(new SerializableCard
            {
                Id = card.Id,
                CurrentUpgradeLevel = card.CurrentUpgradeLevel,
                Enchantment = card.Enchantment,
                Props = match.Props,
                FloorAddedToDeck = card.FloorAddedToDeck
            }, deck[i].Provenance);
        }
    }

    private static int FindCardIndex(List<ProvenancedCard> deck, SerializableCard target)
    {
        int index = deck.FindIndex(pc =>
            pc.Card.Id == target.Id &&
            (!target.FloorAddedToDeck.HasValue || pc.Card.FloorAddedToDeck == target.FloorAddedToDeck));
        return index >= 0 ? index : deck.FindIndex(pc => pc.Card.Id == target.Id);
    }

    private static void RemoveOneMatchingCard(List<ProvenancedCard> deck, SerializableCard target)
    {
        int index = FindCardIndex(deck, target);
        if (index >= 0)
        {
            deck.RemoveAt(index);
        }
    }

    private static void ApplyEnchant(List<ProvenancedCard> deck, CardEnchantmentHistoryEntry entry)
    {
        int index = FindCardIndex(deck, entry.Card);
        if (index < 0)
        {
            return;
        }
        SerializableCard existing = deck[index].Card;
        deck[index] = new ProvenancedCard(new SerializableCard
        {
            Id = existing.Id,
            CurrentUpgradeLevel = existing.CurrentUpgradeLevel,
            Enchantment = entry.Card.Enchantment ?? new SerializableEnchantment { Id = entry.Enchantment, Amount = 1 },
            FloorAddedToDeck = existing.FloorAddedToDeck
        }, Provenance.Replayed);
    }

    /// <summary>Applies one upgrade (delta=+1) or downgrade (delta=-1) event to a same-id copy in the deck.
    /// Per runfiles/SCHEMA.md Q3, upgrade events are bare ids with no per-instance disambiguation among
    /// duplicates; since the deck is a multiset for combat purposes which specific copy doesn't matter, so
    /// we deterministically pick the least- (or most-, for downgrades) upgraded matching copy.</summary>
    private static void ApplyUpgradeDelta(List<ProvenancedCard> deck, ModelId cardId, int delta)
    {
        List<(ProvenancedCard Card, int Index)> candidates = deck
            .Select((pc, i) => (Card: pc, Index: i))
            .Where(t => t.Card.Card.Id == cardId)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        (ProvenancedCard Card, int Index) target = delta > 0
            ? candidates.OrderBy(t => t.Card.Card.CurrentUpgradeLevel).First()
            : candidates.OrderByDescending(t => t.Card.Card.CurrentUpgradeLevel).First();

        SerializableCard c = target.Card.Card;
        deck[target.Index] = new ProvenancedCard(new SerializableCard
        {
            Id = c.Id,
            CurrentUpgradeLevel = Math.Max(0, c.CurrentUpgradeLevel + delta),
            Enchantment = c.Enchantment,
            FloorAddedToDeck = c.FloorAddedToDeck
        }, Provenance.Replayed);
    }

    private static void RemoveOneMatchingRelic(List<ProvenancedRelic> relics, ModelId id)
    {
        int index = relics.FindIndex(pr => pr.Relic.Id == id);
        if (index >= 0)
        {
            relics.RemoveAt(index);
        }
    }

    private static void RemoveOneMatchingPotion(List<ProvenancedPotion> potions, ModelId id)
    {
        int index = potions.FindIndex(p => p.PotionId == id);
        if (index >= 0)
        {
            potions.RemoveAt(index);
        }
    }
}
