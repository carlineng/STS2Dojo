using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
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
        RunHistory run, int globalFloor)
    {
        List<MapPointHistoryEntry> floors = run.MapPointHistory.SelectMany(act => act).ToList();
        if (globalFloor < 1 || globalFloor > floors.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(globalFloor),
                $"Run has {floors.Count} floors; floor {globalFloor} is out of range.");
        }

        MapPointHistoryEntry targetFloor = floors[globalFloor - 1];
        MapPointRoomHistoryEntry combatRoom = targetFloor.Rooms.FirstOrDefault(IsCombatRoom)
            ?? throw new InvalidOperationException(
                $"Floor {globalFloor} has no combat room (room types present: " +
                $"{string.Join(", ", targetFloor.Rooms.Select(r => r.RoomType))}).");
        return (targetFloor, combatRoom);
    }

    /// <summary>
    /// Reconstructs the loadout entering the fight on <paramref name="globalFloor"/>. The starting
    /// deck/relics/HP/gold are supplied by the caller rather than looked up from
    /// <c>CharacterModel</c> here, because the *true* ascension-adjusted starting inventory (e.g. Silent's
    /// Ascender's Bane curse at high ascension) is only known once the game has actually created a player
    /// for that character+ascension — <c>CharacterModel.StartingDeck</c> alone doesn't include it.
    /// </summary>
    public static ReconstructedLoadout Reconstruct(
        RunHistory run,
        int globalFloor,
        IReadOnlyList<SerializableCard> startingDeck,
        IReadOnlyList<SerializableRelic> startingRelics,
        int startingHp,
        int startingGold,
        ulong playerId = 1)
    {
        RunHistoryPlayer finalPlayer = run.Players.Single();
        if (finalPlayer.Id != playerId)
        {
            throw new InvalidOperationException(
                $"Only single-player runs are supported (CLAUDE.md §6); expected player id {playerId}, found {finalPlayer.Id}.");
        }

        List<MapPointHistoryEntry> floors = run.MapPointHistory.SelectMany(act => act).ToList();
        (_, MapPointRoomHistoryEntry combatRoom) = FindCombatFloor(run, globalFloor);
        ModelId encounterId = combatRoom.ModelId
            ?? throw new InvalidOperationException($"Floor {globalFloor}'s combat room is missing model_id.");

        List<ProvenancedCard> deck = startingDeck
            .Select(c => new ProvenancedCard(c, Provenance.Derived))
            .ToList();
        List<ProvenancedRelic> relics = startingRelics
            .Select(r => new ProvenancedRelic(r, Provenance.Derived))
            .ToList();

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
                }
            }

            foreach (ModelId removedId in ps.RelicsRemoved)
            {
                RemoveOneMatchingRelic(relics, removedId);
            }
        }

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
            MonsterIds = combatRoom.MonsterIds.ToList()
        };
    }

    private static bool IsCombatRoom(MapPointRoomHistoryEntry room) =>
        room.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss;

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
}
