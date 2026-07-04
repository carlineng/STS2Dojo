using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Dojo.STS2DojoCode.Reconstruction;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// One editable piece of relic/card state in the Replay Setup modal: which <c>[SavedProperty]</c> it
/// writes, what the player sees, and how the value is clamped. Values are always modeled as ints; bool
/// properties use 0/1 (<see cref="IsBool"/>) and render as No/Yes.
/// </summary>
public sealed class DojoStateSpec
{
    /// <summary>The [SavedProperty] property name <see cref="SavedProperties.Fill"/> resolves by
    /// reflection. A string (not nameof) only where the game declares the property private
    /// (TeaOfDiscourtesy.CombatsLeft, SilkenTress.IsUsed).</summary>
    public required string PropertyName { get; init; }

    /// <summary>Small helper line under the name, e.g. "Attack counter · 10th attack deals double damage".</summary>
    public required string HelperText { get; init; }

    public required int Min { get; init; }
    public required int Max { get; init; }

    /// <summary>The value the row starts at — always the value the model would have with no props filled
    /// (its field initializer), so leaving a row untouched is identical to today's behavior.</summary>
    public required int Default { get; init; }

    /// <summary>What the Primed preset sets: one step before the trigger for threshold counters, the
    /// "will affect this fight" state for charges/bools. Null on card specs — cards render no presets.</summary>
    public int? Primed { get; init; }

    /// <summary>True for bool [SavedProperty]s: value is 0/1, displayed No/Yes.</summary>
    public bool IsBool { get; init; }

    /// <summary>For state split across two linked properties (Genetic Algorithm's CurrentBlock +
    /// IncreasedBlock, The Scythe's CurrentDamage + IncreasedDamage) — builds the full props blob from
    /// the single displayed value.</summary>
    public Func<int, SavedProperties>? BuildPropsOverride { get; init; }

    public int Clamp(int value) => Math.Min(Math.Max(value, Min), Max);

    public SavedProperties BuildProps(int value)
    {
        if (BuildPropsOverride != null)
        {
            return BuildPropsOverride(value);
        }
        var props = new SavedProperties();
        if (IsBool)
        {
            props.bools = [new SavedProperties.SavedProperty<bool>(PropertyName, value != 0)];
        }
        else
        {
            props.ints = [new SavedProperties.SavedProperty<int>(PropertyName, value)];
        }
        return props;
    }
}

/// <summary>
/// The registry behind the Replay Setup modal's RELIC STATE / CARD STATE sections: which relics and
/// cards carry adjustable <c>[SavedProperty]</c> state, and what that state means. Matched by model
/// TYPE (not id string) so a renamed game class breaks the build instead of silently dropping a row.
///
/// Every [SavedProperty]-bearing relic/card in the game (40 relics, 4 cards as of v0.107.1 — found by
/// grepping decompiled/ for the attribute) was reviewed for this table. The ones deliberately NOT here
/// have state a counter can't represent and a single Dojo fight can't observe:
/// <list type="bullet">
/// <item>Map/run-structure state — FurCoat (marked map coords + act), GoldenCompass (golden-path act),
/// WingedBoots-style free travel is kept ONLY because it's a simple counter; SpoilsMap card (quest act
/// index). The Dojo has no map, so this state is inert.</item>
/// <item>Record-keeping for content already baked into the reconstructed loadout — ArchaicTooth
/// (which starter card it transcended), DustyTome (which ancient card it added), TouchOfOrobas (which
/// starter relic it refined), PaelsTooth (which cards it duplicated), SeaGlass (which character's cards
/// it offers). The cards/relics these produced are reconstructed from the run log itself.</item>
/// <item>Cosmetic — Byrdpip / PaelsLegion skins.</item>
/// <item>Auto-reset per combat — LavaLamp.TookDamageThisCombat is set false by its own
/// AfterRoomEntered when the fight starts, so a pre-launch edit can never be observed.</item>
/// <item>Secondary properties on relics already listed — SilverCrucible.TreasureRoomsEntered,
/// WongosMysteryTicket.GaveRelic (the fresh default, false, is the only sensible replay value).</item>
/// </list>
/// Thresholds in helper text/Primed values are read from each model's decompiled source (CanonicalVars
/// / constants), not guessed.
/// </summary>
public static class DojoStatefulContent
{
    // ------------------------------------------------------------------ relic specs

    private static readonly DojoStateSpec PenNibSpec = new()
    {
        PropertyName = nameof(PenNib.AttacksPlayed),
        HelperText = "Attacks played · the 10th attack deals double damage",
        Min = 0, Max = 9, Default = 0, Primed = 9
    };

    private static readonly DojoStateSpec NunchakuSpec = new()
    {
        PropertyName = nameof(Nunchaku.AttacksPlayed),
        HelperText = "Attacks played · gain energy every 10th attack",
        Min = 0, Max = 9, Default = 0, Primed = 9
    };

    private static readonly DojoStateSpec HappyFlowerSpec = new()
    {
        PropertyName = nameof(HappyFlower.TurnsSeen),
        HelperText = "Turns counted · gain energy every 3rd turn",
        Min = 0, Max = 2, Default = 0, Primed = 2
    };

    private static readonly DojoStateSpec FakeHappyFlowerSpec = new()
    {
        PropertyName = nameof(FakeHappyFlower.TurnsSeen),
        HelperText = "Turns counted · gain energy every 5th turn",
        Min = 0, Max = 4, Default = 0, Primed = 4
    };

    private static readonly DojoStateSpec PendulumSpec = new()
    {
        PropertyName = nameof(Pendulum.TurnsSeen),
        HelperText = "Turns counted · draw a card every 3rd turn",
        Min = 0, Max = 2, Default = 0, Primed = 2
    };

    private static readonly DojoStateSpec PollinousCoreSpec = new()
    {
        PropertyName = nameof(PollinousCore.TurnsSeen),
        HelperText = "Turns counted · draw 2 cards every 4th turn",
        Min = 0, Max = 3, Default = 0, Primed = 3
    };

    private static readonly DojoStateSpec TuningForkSpec = new()
    {
        PropertyName = nameof(TuningFork.SkillsPlayed),
        HelperText = "Skills played · gain Block every 10th skill",
        Min = 0, Max = 9, Default = 0, Primed = 9
    };

    private static readonly DojoStateSpec IronClubSpec = new()
    {
        PropertyName = nameof(IronClub.CardsPlayed),
        HelperText = "Cards played · draw a card every 4th card",
        Min = 0, Max = 3, Default = 0, Primed = 3
    };

    private static readonly DojoStateSpec GalacticDustSpec = new()
    {
        PropertyName = nameof(GalacticDust.StarsSpent),
        HelperText = "Stars spent · gain Block per 10 stars",
        Min = 0, Max = 9, Default = 0, Primed = 9
    };

    private static readonly DojoStateSpec GiryaSpec = new()
    {
        PropertyName = nameof(Girya.TimesLifted),
        HelperText = "Times lifted · gain that much Strength at combat start",
        Min = 0, Max = 3, Default = 0, Primed = 3
    };

    private static readonly DojoStateSpec PumpkinCandleSpec = new()
    {
        PropertyName = nameof(PumpkinCandle.KindleCount),
        HelperText = "Kindle charges · +1 energy while lit, burns down each combat",
        Min = 0, Max = 5, Default = 0, Primed = 5
    };

    private static readonly DojoStateSpec BoneTeaSpec = new()
    {
        PropertyName = nameof(BoneTea.CombatsLeft),
        HelperText = "Combats left · upgrades your hand on turn 1",
        Min = 0, Max = 1, Default = 1, Primed = 1
    };

    private static readonly DojoStateSpec EmberTeaSpec = new()
    {
        PropertyName = nameof(EmberTea.CombatsLeft),
        HelperText = "Combats left · gain 2 Strength at combat start",
        Min = 0, Max = 5, Default = 5, Primed = 5
    };

    private static readonly DojoStateSpec TeaOfDiscourtesySpec = new()
    {
        PropertyName = "CombatsLeft", // private on TeaOfDiscourtesy — SavedProperties.Fill resolves NonPublic
        HelperText = "Combats left · shuffles 2 Dazed into your draw pile",
        Min = 0, Max = 1, Default = 1, Primed = 1
    };

    private static readonly DojoStateSpec BookOfFiveRingsSpec = new()
    {
        PropertyName = nameof(BookOfFiveRings.CardsAdded),
        HelperText = "Cards added to deck · heal every 5th card",
        Min = 0, Max = 4, Default = 0, Primed = 4
    };

    private static readonly DojoStateSpec FishingRodSpec = new()
    {
        PropertyName = nameof(FishingRod.CombatsSeen),
        HelperText = "Fights counted · upgrades a random card every 3rd fight won",
        Min = 0, Max = 2, Default = 0, Primed = 2
    };

    private static readonly DojoStateSpec LastingCandySpec = new()
    {
        PropertyName = nameof(LastingCandy.CombatsSeen),
        HelperText = "Fights counted · every 2nd fight's reward offers Powers",
        Min = 0, Max = 1, Default = 0, Primed = 1
    };

    private static readonly DojoStateSpec ToyBoxSpec = new()
    {
        PropertyName = nameof(ToyBox.CombatsSeen),
        HelperText = "Fights counted · melts a Wax relic every 3rd fight",
        Min = 0, Max = 11, Default = 0, Primed = 2
    };

    private static readonly DojoStateSpec SwordOfStoneSpec = new()
    {
        PropertyName = nameof(SwordOfStone.ElitesDefeated),
        HelperText = "Elites defeated · becomes Sword of Jade at 5",
        Min = 0, Max = 4, Default = 0, Primed = 4
    };

    private static readonly DojoStateSpec PaelsWingSpec = new()
    {
        PropertyName = nameof(PaelsWing.RewardsSacrificed),
        HelperText = "Rewards sacrificed · every 2nd grants a relic",
        Min = 0, Max = 1, Default = 0, Primed = 1
    };

    private static readonly DojoStateSpec SilverCrucibleSpec = new()
    {
        PropertyName = nameof(SilverCrucible.TimesUsed),
        HelperText = "Times used · upgrades card rewards until used 3 times",
        Min = 0, Max = 3, Default = 0, Primed = 0
    };

    private static readonly DojoStateSpec WingedBootsSpec = new()
    {
        PropertyName = nameof(WingedBoots.TimesUsed),
        HelperText = "Free-travel charges used (map only — inert in the Dojo)",
        Min = 0, Max = 3, Default = 0, Primed = 0
    };

    private static readonly DojoStateSpec WongosMysteryTicketSpec = new()
    {
        PropertyName = nameof(WongosMysteryTicket.CombatsFinished),
        HelperText = "Fights finished · pays out 3 relics after the 5th",
        Min = 0, Max = 4, Default = 0, Primed = 4
    };

    private static readonly DojoStateSpec LizardTailSpec = new()
    {
        PropertyName = nameof(LizardTail.WasUsed),
        HelperText = "Already used? Unused prevents your death once",
        Min = 0, Max = 1, Default = 0, Primed = 0, IsBool = true
    };

    private static readonly DojoStateSpec VenerableTeaSetSpec = new()
    {
        PropertyName = nameof(VenerableTeaSet.GainEnergyInNextCombat),
        HelperText = "Steeped? Gain 2 energy at the start of this combat",
        Min = 0, Max = 1, Default = 0, Primed = 1, IsBool = true
    };

    private static readonly DojoStateSpec FakeVenerableTeaSetSpec = new()
    {
        PropertyName = nameof(FakeVenerableTeaSet.GainEnergyInNextCombat),
        HelperText = "Steeped? Gain 1 energy at the start of this combat",
        Min = 0, Max = 1, Default = 0, Primed = 1, IsBool = true
    };

    private static readonly DojoStateSpec MawBankSpec = new()
    {
        PropertyName = nameof(MawBank.HasItemBeenBought),
        HelperText = "Spent? While unspent, grants 12 gold each room entered",
        Min = 0, Max = 1, Default = 0, Primed = 0, IsBool = true
    };

    private static readonly DojoStateSpec LavaRockSpec = new()
    {
        PropertyName = nameof(LavaRock.HasTriggered),
        HelperText = "Already gave its Act 1 boss relics? (reward-time — inert in the Dojo)",
        Min = 0, Max = 1, Default = 0, Primed = 0, IsBool = true
    };

    private static readonly DojoStateSpec SilkenTressSpec = new()
    {
        PropertyName = "IsUsed", // private on SilkenTress — SavedProperties.Fill resolves NonPublic
        HelperText = "Already glammed a card reward? (reward-time — inert in the Dojo)",
        Min = 0, Max = 1, Default = 0, Primed = 0, IsBool = true
    };

    // ------------------------------------------------------------------ card specs

    private static readonly DojoStateSpec GeneticAlgorithmSpec = new()
    {
        PropertyName = nameof(GeneticAlgorithm.CurrentBlock),
        HelperText = "Block gained when played",
        Min = 1, Max = 999, Default = 1,
        BuildPropsOverride = value => new SavedProperties
        {
            ints =
            [
                new SavedProperties.SavedProperty<int>(nameof(GeneticAlgorithm.CurrentBlock), value),
                // Kept consistent with CurrentBlock (the game maintains CurrentBlock = 1 + IncreasedBlock)
                // so a downgrade/recompute inside combat lands back on the edited value.
                new SavedProperties.SavedProperty<int>(nameof(GeneticAlgorithm.IncreasedBlock), value - 1)
            ]
        }
    };

    private static readonly DojoStateSpec TheScytheSpec = new()
    {
        PropertyName = nameof(TheScythe.CurrentDamage),
        HelperText = "Damage dealt when played",
        Min = 13, Max = 999, Default = 13,
        BuildPropsOverride = value => new SavedProperties
        {
            ints =
            [
                new SavedProperties.SavedProperty<int>(nameof(TheScythe.CurrentDamage), value),
                // CurrentDamage = 13 + IncreasedDamage in the game's own recompute path.
                new SavedProperties.SavedProperty<int>(nameof(TheScythe.IncreasedDamage), value - 13)
            ]
        }
    };

    private static readonly DojoStateSpec GuiltySpec = new()
    {
        PropertyName = nameof(Guilty.CombatsSeen),
        HelperText = "Combats endured · removes itself after the 5th",
        Min = 0, Max = 4, Default = 0
    };

    // ------------------------------------------------------------------ lookup

    /// <summary>The editable state on this relic, or null if it has none (or none a Dojo fight can
    /// observe — see the class doc for what's deliberately excluded).</summary>
    public static DojoStateSpec? ForRelic(RelicModel model) => model switch
    {
        PenNib _ => PenNibSpec,
        Nunchaku _ => NunchakuSpec,
        HappyFlower _ => HappyFlowerSpec,
        FakeHappyFlower _ => FakeHappyFlowerSpec,
        Pendulum _ => PendulumSpec,
        PollinousCore _ => PollinousCoreSpec,
        TuningFork _ => TuningForkSpec,
        IronClub _ => IronClubSpec,
        GalacticDust _ => GalacticDustSpec,
        Girya _ => GiryaSpec,
        PumpkinCandle _ => PumpkinCandleSpec,
        BoneTea _ => BoneTeaSpec,
        EmberTea _ => EmberTeaSpec,
        TeaOfDiscourtesy _ => TeaOfDiscourtesySpec,
        BookOfFiveRings _ => BookOfFiveRingsSpec,
        FishingRod _ => FishingRodSpec,
        LastingCandy _ => LastingCandySpec,
        ToyBox _ => ToyBoxSpec,
        SwordOfStone _ => SwordOfStoneSpec,
        PaelsWing _ => PaelsWingSpec,
        SilverCrucible _ => SilverCrucibleSpec,
        WingedBoots _ => WingedBootsSpec,
        WongosMysteryTicket _ => WongosMysteryTicketSpec,
        LizardTail _ => LizardTailSpec,
        VenerableTeaSet _ => VenerableTeaSetSpec,
        FakeVenerableTeaSet _ => FakeVenerableTeaSetSpec,
        MawBank _ => MawBankSpec,
        LavaRock _ => LavaRockSpec,
        SilkenTress _ => SilkenTressSpec,
        _ => null
    };

    /// <summary>The editable state on this card, or null if it has none (SpoilsMap's quest act index is
    /// map-only state — see the class doc).</summary>
    public static DojoStateSpec? ForCard(CardModel model) => model switch
    {
        GeneticAlgorithm _ => GeneticAlgorithmSpec,
        TheScythe _ => TheScytheSpec,
        Guilty _ => GuiltySpec,
        _ => null
    };
}

/// <summary>
/// The Replay Setup modal's output: per-relic and per-card <see cref="SavedProperties"/> blobs to stamp
/// onto the reconstructed loadout at launch. Relics are unique per run, so they key by id; cards key by
/// (id, occurrence index among same-id cards in deck order), which is stable between the modal's preview
/// reconstruction and the launch-time reconstruction because both replay the same run log over the same
/// (character, ascension) starting inventory, and the reconstructor replaces deck entries in place.
/// </summary>
public sealed class DojoStateAdjustments
{
    private readonly Dictionary<ModelId, SavedProperties> _relicProps = new();
    private readonly Dictionary<(ModelId Id, int Occurrence), SavedProperties> _cardProps = new();

    public int Count => _relicProps.Count + _cardProps.Count;

    public void SetRelic(ModelId relicId, SavedProperties props) => _relicProps[relicId] = props;

    public void SetCard(ModelId cardId, int occurrence, SavedProperties props) =>
        _cardProps[(cardId, occurrence)] = props;

    /// <summary>Stamps the adjustment props onto the loadout's serializable DTOs, so they flow into the
    /// live models through the exact pipeline the game itself restores saved state with
    /// (RelicModel/CardModel.FromSerializable → SavedProperties.Fill). Only call this on a launch-time
    /// loadout (whose DTOs are freshly built per launch) — never on the modal's preview loadout, whose
    /// Derived entries alias DojoFloorEligibility's shared snapshot cache.</summary>
    public void ApplyTo(ReconstructedLoadout loadout)
    {
        foreach (ProvenancedRelic pr in loadout.Relics)
        {
            if (pr.Relic.Id != null && _relicProps.TryGetValue(pr.Relic.Id, out SavedProperties? props))
            {
                pr.Relic.Props = props;
            }
        }

        var occurrences = new Dictionary<ModelId, int>();
        foreach (ProvenancedCard pc in loadout.Deck)
        {
            if (pc.Card.Id == null)
            {
                continue;
            }
            occurrences.TryGetValue(pc.Card.Id, out int occurrence);
            occurrences[pc.Card.Id] = occurrence + 1;
            if (_cardProps.TryGetValue((pc.Card.Id, occurrence), out SavedProperties? props))
            {
                pc.Card.Props = props;
            }
        }
    }
}
