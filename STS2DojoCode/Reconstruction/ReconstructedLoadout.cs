using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

/// <summary>
/// Where a reconstructed field's value came from (see CLAUDE.md §5). <c>Derived</c>: read straight off
/// the character definition or a per-floor snapshot with no replay logic involved. <c>Replayed</c>:
/// produced by forward-walking floor deltas (cards_gained/removed/transformed/upgraded/enchanted,
/// relic choices). <c>Assumed</c>: unrecoverable from the save, defaulted (e.g. potions in v1).
/// </summary>
public enum Provenance
{
    Derived,
    Replayed,
    Assumed
}

public readonly record struct ProvenancedCard(SerializableCard Card, Provenance Provenance);

public readonly record struct ProvenancedRelic(SerializableRelic Relic, Provenance Provenance);

public readonly record struct ProvenancedPotion(ModelId PotionId, Provenance Provenance);

/// <summary>
/// The reconstructor's output: player state entering the fight on a given global floor, plus the
/// encounter to launch. Uses the game's own <see cref="SerializableCard"/>/<see cref="SerializableRelic"/>
/// DTOs so it can be handed straight to <c>CardModel.FromSerializable</c>/<c>RelicModel.FromSerializable</c>.
/// </summary>
public class ReconstructedLoadout
{
    public required ModelId CharacterId { get; init; }
    public required int Ascension { get; init; }
    public required int CurrentHp { get; init; }
    public required int MaxHp { get; init; }
    public required int Gold { get; init; }
    public required List<ProvenancedCard> Deck { get; init; }
    public required List<ProvenancedRelic> Relics { get; init; }
    public required ModelId EncounterId { get; init; }
    public required List<ModelId> MonsterIds { get; init; }

    /// <summary>Potions entering the fight (see CLAUDE.md §5d).</summary>
    public List<ProvenancedPotion> Potions { get; init; } = [];

    /// <summary>Max potion slots entering the fight: base 3, minus 1 under Ascension's Tight Belt
    /// (ascension >= 4), plus any one-time bonus from Potion Belt/Alchemical Coffer/Phial Holster picked
    /// up along the way (see RunReconstructor.PotionSlotGrantingRelics).</summary>
    public required int MaxPotionSlots { get; init; }
}
