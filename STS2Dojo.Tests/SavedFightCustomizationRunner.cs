using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>Pure-logic tests for the Replay Fight modal's customization output — seed override
/// semantics, props reading/merging, and the never-mutate-the-library-payload guarantee.</summary>
internal static class SavedFightCustomizationRunner
{
    public static void Run()
    {
        RunGroup("captured prop reads", CapturedPropReads);
        RunGroup("props merge", PropsMerge);
        RunGroup("seed override drops rng counters", SeedOverrideDropsRngCounters);
        RunGroup("relic overlay clones only edited entries", RelicOverlayClonesOnlyEditedEntries);
        RunGroup("card overlay keys by occurrence", CardOverlayKeysByOccurrence);
        RunGroup("empty customization is a faithful copy", EmptyCustomizationIsFaithfulCopy);

        Console.WriteLine();
        Console.WriteLine("6 saved-fight customization test groups passed.");
    }

    private static void RunGroup(string name, Action test)
    {
        test();
        Console.WriteLine("PASS " + name);
    }

    /// <summary>A payload with the shapes the modal actually meets: a stateful relic whose captured
    /// props also carry a property the modal doesn't expose (SilverCrucible.TreasureRoomsEntered), a
    /// props-less relic/card (state at its default is never serialized), and two copies of the same
    /// stateful card with different captured values.</summary>
    private static SharedFightPayload SamplePayload() => new()
    {
        GameBuildId = "v0.107.1",
        ModVersion = "0.1.0",
        Title = "Sample fight",
        Comment = "sample",
        Author = "SpireFan42",
        CreatedUtc = new DateTime(2026, 7, 7, 8, 0, 0, DateTimeKind.Utc),
        CharacterId = ModelId.Deserialize("CHARACTER.SILENT"),
        Ascension = 10,
        EncounterId = ModelId.Deserialize("ENCOUNTER.DECIMILLIPEDE_ELITE"),
        Seed = "SAVEDSEED1",
        RunRng = new SerializableRunRngSet { Seed = "SAVEDSEED1" },
        PlayerRng = new SerializablePlayerRngSet { Seed = 987654321u },
        Deck =
        [
            new SerializableCard
            {
                Id = ModelId.Deserialize("CARD.GENETIC_ALGORITHM"),
                Props = new SavedProperties
                {
                    ints =
                    [
                        new SavedProperties.SavedProperty<int>("CurrentBlock", 5),
                        new SavedProperties.SavedProperty<int>("IncreasedBlock", 4)
                    ]
                }
            },
            new SerializableCard { Id = ModelId.Deserialize("CARD.STRIKE_SILENT") },
            new SerializableCard
            {
                Id = ModelId.Deserialize("CARD.GENETIC_ALGORITHM"),
                CurrentUpgradeLevel = 1,
                Props = new SavedProperties
                {
                    ints =
                    [
                        new SavedProperties.SavedProperty<int>("CurrentBlock", 9),
                        new SavedProperties.SavedProperty<int>("IncreasedBlock", 8)
                    ]
                }
            },
        ],
        Relics =
        [
            new SerializableRelic
            {
                Id = ModelId.Deserialize("RELIC.PEN_NIB"),
                Props = new SavedProperties
                {
                    ints = [new SavedProperties.SavedProperty<int>("AttacksPlayed", 7)]
                }
            },
            new SerializableRelic
            {
                Id = ModelId.Deserialize("RELIC.SILVER_CRUCIBLE"),
                Props = new SavedProperties
                {
                    ints =
                    [
                        new SavedProperties.SavedProperty<int>("TimesUsed", 2),
                        new SavedProperties.SavedProperty<int>("TreasureRoomsEntered", 3)
                    ]
                }
            },
            new SerializableRelic { Id = ModelId.Deserialize("RELIC.LIZARD_TAIL") },
        ],
        Potions = [new SerializablePotion { Id = ModelId.Deserialize("POTION.FOUL_POTION"), SlotIndex = 0 }],
        MaxPotionSlots = 2,
        CurrentHp = 30,
        MaxHp = 70,
        Gold = 55,
    };

    private static int GetInt(SavedProperties? props, string name, string label)
    {
        Assert.True(SavedFightCustomization.TryGetInt(props, name, out int value), label + ": property present");
        return value;
    }

    private static void CapturedPropReads()
    {
        SharedFightPayload payload = SamplePayload();

        Assert.Equal(7, GetInt(payload.Relics[0].Props, "AttacksPlayed", "pen nib counter"), "pen nib counter");
        Assert.True(!SavedFightCustomization.TryGetInt(payload.Relics[0].Props, "TimesUsed", out _),
            "absent property reads false");
        Assert.True(!SavedFightCustomization.TryGetInt(payload.Relics[2].Props, "WasUsed", out _),
            "null props reads false");

        var boolProps = new SavedProperties
        {
            bools = [new SavedProperties.SavedProperty<bool>("WasUsed", true)]
        };
        Assert.True(SavedFightCustomization.TryGetBool(boolProps, "WasUsed", out bool used) && used,
            "bool property present");
        Assert.True(!SavedFightCustomization.TryGetBool(boolProps, "Other", out _), "absent bool reads false");
        Assert.True(!SavedFightCustomization.TryGetBool(null, "WasUsed", out _), "null props bool reads false");
    }

    private static void PropsMerge()
    {
        var captured = new SavedProperties
        {
            ints =
            [
                new SavedProperties.SavedProperty<int>("TimesUsed", 2),
                new SavedProperties.SavedProperty<int>("TreasureRoomsEntered", 3)
            ],
            bools = [new SavedProperties.SavedProperty<bool>("WasUsed", true)],
            strings = [new SavedProperties.SavedProperty<string>("SkinName", "gold")],
        };
        var overlay = new SavedProperties
        {
            ints = [new SavedProperties.SavedProperty<int>("TimesUsed", 0)],
            bools = [new SavedProperties.SavedProperty<bool>("WasUsed", false)],
        };

        SavedProperties merged = SavedFightCustomization.MergeProps(captured, overlay);

        Assert.Equal(0, GetInt(merged, "TimesUsed", "edited int replaced"), "edited int replaced");
        Assert.Equal(3, GetInt(merged, "TreasureRoomsEntered", "unrelated int preserved"),
            "unrelated int preserved");
        Assert.True(SavedFightCustomization.TryGetBool(merged, "WasUsed", out bool used) && !used,
            "edited bool replaced");
        Assert.Equal(1, merged.strings!.Count, "unrelated string list carried across");
        Assert.Equal("gold", merged.strings[0].value, "unrelated string value intact");

        // The captured blob itself must not be modified (it belongs to the library payload).
        Assert.Equal(2, GetInt(captured, "TimesUsed", "captured int untouched"), "captured int untouched");
        Assert.True(SavedFightCustomization.TryGetBool(captured, "WasUsed", out bool origUsed) && origUsed,
            "captured bool untouched");

        // Null captured props: the merge is just the overlay, with a new-property append.
        SavedProperties fromNull = SavedFightCustomization.MergeProps(null, overlay);
        Assert.Equal(0, GetInt(fromNull, "TimesUsed", "overlay-only int"), "overlay-only int");
        Assert.True(!SavedFightCustomization.TryGetInt(fromNull, "TreasureRoomsEntered", out _),
            "no phantom entries from null captured");
    }

    private static void SeedOverrideDropsRngCounters()
    {
        SharedFightPayload source = SamplePayload();

        var changed = new SavedFightCustomization { SeedOverride = "NEWSEED123" };
        Assert.True(changed.ChangesAnything(source), "new seed counts as a change");
        SharedFightPayload result = changed.BuildCustomizedPayload(source);
        Assert.Equal("NEWSEED123", result.Seed, "seed replaced");
        Assert.True(result.RunRng == null, "run RNG counters dropped with new seed");
        Assert.True(result.PlayerRng == null, "player RNG counters dropped with new seed");
        Assert.Equal("SAVEDSEED1", source.Seed, "source seed untouched");
        Assert.True(source.RunRng != null, "source counters untouched");

        var same = new SavedFightCustomization { SeedOverride = source.Seed };
        Assert.True(!same.ChangesAnything(source), "same seed is not a change");
        SharedFightPayload kept = same.BuildCustomizedPayload(source);
        Assert.Equal(source.Seed, kept.Seed, "same seed kept");
        Assert.True(ReferenceEquals(source.RunRng, kept.RunRng), "run counters kept with same seed");
        Assert.True(ReferenceEquals(source.PlayerRng, kept.PlayerRng), "player counters kept with same seed");
    }

    private static void RelicOverlayClonesOnlyEditedEntries()
    {
        SharedFightPayload source = SamplePayload();

        var custom = new SavedFightCustomization();
        custom.SetRelic(ModelId.Deserialize("RELIC.SILVER_CRUCIBLE"), new SavedProperties
        {
            ints = [new SavedProperties.SavedProperty<int>("TimesUsed", 0)]
        });
        Assert.True(custom.ChangesAnything(source), "state edit counts as a change");

        SharedFightPayload result = custom.BuildCustomizedPayload(source);

        Assert.True(ReferenceEquals(source.Relics[0], result.Relics[0]), "unedited relic shared by reference");
        Assert.True(ReferenceEquals(source.Relics[2], result.Relics[2]), "props-less relic shared by reference");
        Assert.True(!ReferenceEquals(source.Relics[1], result.Relics[1]), "edited relic cloned");

        Assert.Equal(0, GetInt(result.Relics[1].Props, "TimesUsed", "edited value stamped"),
            "edited value stamped");
        Assert.Equal(3, GetInt(result.Relics[1].Props, "TreasureRoomsEntered", "unexposed captured prop survives"),
            "unexposed captured prop survives");

        // The library payload's own entry keeps its captured value.
        Assert.Equal(2, GetInt(source.Relics[1].Props, "TimesUsed", "source relic untouched"),
            "source relic untouched");
        Assert.Equal("SAVEDSEED1", result.Seed, "seed unchanged without an override");
        Assert.True(ReferenceEquals(source.RunRng, result.RunRng), "counters kept without a seed override");
    }

    private static void CardOverlayKeysByOccurrence()
    {
        SharedFightPayload source = SamplePayload();

        // Edit only the SECOND Genetic Algorithm copy (occurrence 1, deck index 2).
        var custom = new SavedFightCustomization();
        custom.SetCard(ModelId.Deserialize("CARD.GENETIC_ALGORITHM"), occurrence: 1, new SavedProperties
        {
            ints =
            [
                new SavedProperties.SavedProperty<int>("CurrentBlock", 20),
                new SavedProperties.SavedProperty<int>("IncreasedBlock", 19)
            ]
        });

        SharedFightPayload result = custom.BuildCustomizedPayload(source);

        Assert.True(ReferenceEquals(source.Deck[0], result.Deck[0]), "first copy shared by reference");
        Assert.True(ReferenceEquals(source.Deck[1], result.Deck[1]), "unrelated card shared by reference");
        Assert.True(!ReferenceEquals(source.Deck[2], result.Deck[2]), "second copy cloned");

        Assert.Equal(20, GetInt(result.Deck[2].Props, "CurrentBlock", "second copy edited"), "second copy edited");
        Assert.Equal(5, GetInt(result.Deck[0].Props, "CurrentBlock", "first copy untouched"), "first copy untouched");
        Assert.Equal(9, GetInt(source.Deck[2].Props, "CurrentBlock", "source card untouched"), "source card untouched");
        Assert.Equal(1, result.Deck[2].CurrentUpgradeLevel, "clone keeps upgrade level");
        Assert.Equal(source.Deck[2].Id, result.Deck[2].Id, "clone keeps id");
    }

    private static void EmptyCustomizationIsFaithfulCopy()
    {
        SharedFightPayload source = SamplePayload();
        var custom = new SavedFightCustomization();

        Assert.True(!custom.ChangesAnything(source), "empty customization changes nothing");

        SharedFightPayload copy = custom.BuildCustomizedPayload(source);
        Assert.Equal(source.SchemaVersion, copy.SchemaVersion, "schema version copied");
        Assert.Equal(source.GameBuildId, copy.GameBuildId, "build id copied");
        Assert.Equal(source.Title, copy.Title, "title copied");
        Assert.Equal(source.Author, copy.Author, "author copied");
        Assert.Equal(source.CharacterId, copy.CharacterId, "character copied");
        Assert.Equal(source.Ascension, copy.Ascension, "ascension copied");
        Assert.Equal(source.EncounterId, copy.EncounterId, "encounter copied");
        Assert.Equal(source.Seed, copy.Seed, "seed copied");
        Assert.True(ReferenceEquals(source.RunRng, copy.RunRng), "run counters shared");
        Assert.True(ReferenceEquals(source.PlayerRng, copy.PlayerRng), "player counters shared");
        Assert.Equal(source.Deck.Count, copy.Deck.Count, "deck size");
        for (int i = 0; i < source.Deck.Count; i++)
        {
            Assert.True(ReferenceEquals(source.Deck[i], copy.Deck[i]), $"deck[{i}] shared by reference");
        }
        Assert.Equal(source.Relics.Count, copy.Relics.Count, "relic count");
        for (int i = 0; i < source.Relics.Count; i++)
        {
            Assert.True(ReferenceEquals(source.Relics[i], copy.Relics[i]), $"relics[{i}] shared by reference");
        }
        Assert.Equal(source.Potions.Count, copy.Potions.Count, "potion count");
        Assert.Equal(source.MaxPotionSlots, copy.MaxPotionSlots, "potion slots copied");
        Assert.Equal(source.CurrentHp, copy.CurrentHp, "current hp copied");
        Assert.Equal(source.MaxHp, copy.MaxHp, "max hp copied");
        Assert.Equal(source.Gold, copy.Gold, "gold copied");
    }
}
