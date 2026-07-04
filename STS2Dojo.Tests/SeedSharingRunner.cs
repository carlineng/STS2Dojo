using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>Pure-logic tests for the §12 shared-fight payload/codec/gate (no game assembly, per §5b).</summary>
internal static class SeedSharingRunner
{
    public static void Run()
    {
        RunGroup("snapshot to payload mapping", SnapshotToPayloadMapping);
        RunGroup("payload JSON round-trip", JsonRoundTrip);
        RunGroup("payload code round-trip", CodeRoundTrip);
        RunGroup("paste parsing accepts both forms", ParseAcceptsBothForms);
        RunGroup("malformed input errors", MalformedInputs);
        RunGroup("structural validation", StructuralValidation);
        RunGroup("compatibility gate", CompatibilityGate);
        RunGroup("launch options mapping", LaunchOptionsMapping);

        Console.WriteLine();
        Console.WriteLine("8 seed-sharing test groups passed.");
    }

    private static void RunGroup(string name, Action test)
    {
        test();
        Console.WriteLine("PASS " + name);
    }

    private static readonly DateTime SampleCreatedUtc = new(2026, 7, 4, 12, 34, 56, DateTimeKind.Utc);

    private static DojoFightSnapshot SampleSnapshot()
    {
        SerializableRunRngSet runRng = new() { Seed = "ABC123XYZ0" };
        int i = 1;
        foreach (RunRngType type in Enum.GetValues<RunRngType>())
        {
            runRng.Counters[type] = i++ * 3;
        }

        SerializablePlayerRngSet playerRng = new() { Seed = 987654321u };
        foreach (PlayerRngType type in Enum.GetValues<PlayerRngType>())
        {
            playerRng.Counters[type] = i++ * 7;
        }

        return new DojoFightSnapshot
        {
            Seed = "ABC123XYZ0",
            RunRng = runRng,
            PlayerRng = playerRng,
            CharacterId = ModelId.Deserialize("CHARACTER.SILENT"),
            Ascension = 10,
            EncounterId = ModelId.Deserialize("ENCOUNTER.DECIMILLIPEDE_ELITE"),
            Deck =
            [
                new SerializableCard { Id = ModelId.Deserialize("CARD.STRIKE_SILENT") },
                new SerializableCard
                {
                    Id = ModelId.Deserialize("CARD.NEUTRALIZE"),
                    CurrentUpgradeLevel = 1,
                    Enchantment = new SerializableEnchantment
                    {
                        Id = ModelId.Deserialize("ENCHANTMENT.ECHOING"), Amount = 2
                    },
                    Props = new SavedProperties
                    {
                        ints = [new SavedProperties.SavedProperty<int>("TimesPlayed", 4)]
                    }
                },
            ],
            Relics =
            [
                new SerializableRelic
                {
                    Id = ModelId.Deserialize("RELIC.SILKEN_TRESS"),
                    Props = new SavedProperties
                    {
                        bools = [new SavedProperties.SavedProperty<bool>("IsUsed", true)],
                        ints = [new SavedProperties.SavedProperty<int>("Counter", 7)]
                    }
                },
            ],
            Potions = [new SerializablePotion { Id = ModelId.Deserialize("POTION.FOUL_POTION"), SlotIndex = 1 }],
            MaxPotionSlots = 2,
            CurrentHp = 30,
            MaxHp = 70,
            Gold = 55,
        };
    }

    private static SharedFightPayload SamplePayload() => SharedFightPayload.FromSnapshot(
        SampleSnapshot(),
        gameBuildId: "v0.107.1",
        modVersion: "0.1.0",
        title: "Decimillipede practice",
        comment: "watch the back segment",
        createdUtc: SampleCreatedUtc);

    private static void SnapshotToPayloadMapping()
    {
        SharedFightPayload payload = SamplePayload();

        Assert.Equal(SharedFightPayload.CurrentSchemaVersion, payload.SchemaVersion, "schema stamped");
        Assert.Equal("v0.107.1", payload.GameBuildId, "game build");
        Assert.Equal("0.1.0", payload.ModVersion, "mod version");
        Assert.Equal("Decimillipede practice", payload.Title, "title");
        Assert.Equal("ABC123XYZ0", payload.Seed, "seed");
        Assert.Equal("CHARACTER.SILENT", payload.CharacterId?.ToString(), "character");
        Assert.Equal("ENCOUNTER.DECIMILLIPEDE_ELITE", payload.EncounterId?.ToString(), "encounter");
        Assert.Equal(2, payload.Deck.Count, "deck count");
        Assert.Equal(1, payload.Relics.Count, "relic count");
        Assert.Equal(0, payload.GetStructuralProblems().Count, "sample payload is structurally valid");
    }

    private static void JsonRoundTrip()
    {
        SharedFightPayload original = SamplePayload();
        string json = SharedFightCodec.ToJson(original);

        // Lock the wire format: snake_case property names, snake_case enum-keyed counter dicts, and the
        // exact SavedProperties field shape real `.run` files use.
        Assert.True(json.Contains("\"payload_schema_version\""), "snake_case payload fields");
        Assert.True(json.Contains("\"up_front\""), "run rng counter keys are snake_case");
        Assert.True(json.Contains("\"transformations\""), "player rng counter keys are snake_case");
        Assert.True(json.Contains("\"name\": \"IsUsed\"") || json.Contains("\"name\":\"IsUsed\""),
            "SavedProperties serializes its name/value fields");

        SharedFightPayload restored = SharedFightCodec.FromJson(json);
        AssertPayloadsEquivalent(original, restored, "json");
    }

    private static void CodeRoundTrip()
    {
        SharedFightPayload original = SamplePayload();
        string code = SharedFightCodec.ToCode(original);

        Assert.True(code.StartsWith(SharedFightCodec.CodePrefix), "code carries the transport prefix");
        Assert.True(!code.Contains('{'), "code is not raw JSON");

        AssertPayloadsEquivalent(original, SharedFightCodec.FromCode(code), "code");

        // Chat clients wrap pastes in whitespace/newlines; decoding must shrug that off.
        string mangled = "  " + code[..20] + "\n" + code[20..45] + " \t" + code[45..] + "\n";
        AssertPayloadsEquivalent(original, SharedFightCodec.FromCode(mangled), "whitespace-mangled code");
    }

    private static void ParseAcceptsBothForms()
    {
        SharedFightPayload original = SamplePayload();
        AssertPayloadsEquivalent(original, SharedFightCodec.Parse(SharedFightCodec.ToJson(original)), "parse json");
        AssertPayloadsEquivalent(original, SharedFightCodec.Parse(SharedFightCodec.ToCode(original)), "parse code");
    }

    private static void MalformedInputs()
    {
        AssertFormatError(() => SharedFightCodec.Parse(""), "empty paste");
        AssertFormatError(() => SharedFightCodec.Parse("   \n\t "), "whitespace paste");
        AssertFormatError(() => SharedFightCodec.Parse("complete garbage"), "garbage paste");
        AssertFormatError(() => SharedFightCodec.FromCode("STS2DOJO1.!!!notbase64!!!"), "invalid base64");
        AssertFormatError(
            () => SharedFightCodec.FromCode("STS2DOJO1." + Convert.ToBase64String("not gzip"u8.ToArray())),
            "not gzip");
        AssertFormatError(() => SharedFightCodec.FromJson("{\"seed\": tru"), "truncated json");
        AssertFormatError(() => SharedFightCodec.FromJson("null"), "null json");

        // A future transport version must produce the this-mod-is-too-old message, not a base64 error.
        try
        {
            SharedFightCodec.FromCode("STS2DOJO9.AAAA");
            throw new TestFailureException("future transport: expected SharedFightFormatException");
        }
        catch (SharedFightFormatException e)
        {
            Assert.True(e.Message.Contains("different version"), "future transport message mentions version");
        }
    }

    private static void StructuralValidation()
    {
        // Every decode path runs GetStructuralProblems — an empty object decodes but must be refused.
        AssertFormatError(() => SharedFightCodec.FromJson("{}"), "empty payload object");

        SharedFightPayload payload = SamplePayload();
        payload.RunRng!.Seed = "DIFFERENT0";
        Assert.True(
            payload.GetStructuralProblems().Any(p => p.Contains("seed does not match")),
            "run rng seed mismatch is caught before the game's LoadFromSerializable would throw");

        SharedFightPayload noDeck = SamplePayload();
        noDeck.Deck.Clear();
        Assert.True(noDeck.GetStructuralProblems().Contains("empty deck"), "empty deck is caught");

        SharedFightPayload badHp = SamplePayload();
        badHp.CurrentHp = 999;
        Assert.True(badHp.GetStructuralProblems().Any(p => p.Contains("invalid HP")), "hp above max is caught");
    }

    private static void CompatibilityGate()
    {
        SharedFightPayload payload = SamplePayload();

        Assert.Equal(null, SharedFightGate.GetRefusal(payload, "v0.107.1"), "matching build+schema passes");
        Assert.Equal(null, SharedFightGate.GetRefusal(payload, " v0.107.1 "), "build compare trims whitespace");

        string? buildRefusal = SharedFightGate.GetRefusal(payload, "v0.108.0");
        Assert.True(buildRefusal != null, "build mismatch refuses");
        Assert.True(buildRefusal!.Contains("v0.107.1") && buildRefusal.Contains("v0.108.0"),
            "build refusal names both versions");

        SharedFightPayload newer = SamplePayload();
        newer.SchemaVersion = SharedFightPayload.CurrentSchemaVersion + 1;
        string? newerRefusal = SharedFightGate.GetRefusal(newer, "v0.107.1");
        Assert.True(newerRefusal != null && newerRefusal.Contains("newer"), "newer schema refuses with update hint");

        SharedFightPayload older = SamplePayload();
        older.SchemaVersion = 0;
        string? olderRefusal = SharedFightGate.GetRefusal(older, "v0.107.1");
        Assert.True(olderRefusal != null && olderRefusal.Contains("older"), "older schema refuses");

        // Mod version is diagnostics-only (§12c revised): never part of the gate.
        SharedFightPayload otherMod = SamplePayload();
        otherMod.ModVersion = "99.99.99";
        Assert.Equal(null, SharedFightGate.GetRefusal(otherMod, "v0.107.1"), "mod version mismatch is allowed");
    }

    private static void LaunchOptionsMapping()
    {
        SharedFightPayload payload = SamplePayload();
        DojoLaunchOptions options = payload.ToLaunchOptions();

        Assert.Equal(payload.Seed, options.SeedOverride, "seed override");
        Assert.True(ReferenceEquals(payload.RunRng, options.RunRngCounters), "run counters passed through");
        Assert.True(ReferenceEquals(payload.PlayerRng, options.PlayerRngCounters), "player counters passed through");

        DojoLaunchOptions defaults = DojoLaunchOptions.Default;
        Assert.True(
            defaults.SeedOverride == null && defaults.RunRngCounters == null && defaults.PlayerRngCounters == null,
            "default options mean fresh-RNG normal behavior");
    }

    private static void AssertFormatError(Action action, string label)
    {
        try
        {
            action();
        }
        catch (SharedFightFormatException e)
        {
            Assert.True(!string.IsNullOrWhiteSpace(e.Message), label + ": error message is presentable");
            return;
        }
        catch (Exception e)
        {
            throw new TestFailureException(
                $"{label}: expected SharedFightFormatException, got {e.GetType().Name}: {e.Message}");
        }

        throw new TestFailureException(label + ": expected SharedFightFormatException");
    }

    private static void AssertPayloadsEquivalent(SharedFightPayload expected, SharedFightPayload actual, string label)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion, label + " schema");
        Assert.Equal(expected.GameBuildId, actual.GameBuildId, label + " build");
        Assert.Equal(expected.ModVersion, actual.ModVersion, label + " mod version");
        Assert.Equal(expected.Title, actual.Title, label + " title");
        Assert.Equal(expected.Comment, actual.Comment, label + " comment");
        Assert.Equal(expected.CreatedUtc, actual.CreatedUtc, label + " created");
        Assert.Equal(expected.CharacterId, actual.CharacterId, label + " character");
        Assert.Equal(expected.Ascension, actual.Ascension, label + " ascension");
        Assert.Equal(expected.EncounterId, actual.EncounterId, label + " encounter");
        Assert.Equal(expected.Seed, actual.Seed, label + " seed");
        Assert.Equal(expected.MaxPotionSlots, actual.MaxPotionSlots, label + " potion slots");
        Assert.Equal(expected.CurrentHp, actual.CurrentHp, label + " current hp");
        Assert.Equal(expected.MaxHp, actual.MaxHp, label + " max hp");
        Assert.Equal(expected.Gold, actual.Gold, label + " gold");

        Assert.Equal(expected.RunRng!.Seed, actual.RunRng!.Seed, label + " run rng seed");
        foreach (RunRngType type in Enum.GetValues<RunRngType>())
        {
            Assert.Equal(expected.RunRng.Counters[type], actual.RunRng.Counters[type], $"{label} run counter {type}");
        }
        Assert.Equal(expected.PlayerRng!.Seed, actual.PlayerRng!.Seed, label + " player rng seed");
        foreach (PlayerRngType type in Enum.GetValues<PlayerRngType>())
        {
            Assert.Equal(expected.PlayerRng.Counters[type], actual.PlayerRng.Counters[type],
                $"{label} player counter {type}");
        }

        Assert.Equal(expected.Deck.Count, actual.Deck.Count, label + " deck count");
        for (int i = 0; i < expected.Deck.Count; i++)
        {
            Assert.Equal(expected.Deck[i].Id, actual.Deck[i].Id, $"{label} deck[{i}] id");
            Assert.Equal(expected.Deck[i].CurrentUpgradeLevel, actual.Deck[i].CurrentUpgradeLevel,
                $"{label} deck[{i}] upgrade");
            Assert.Equal(expected.Deck[i].Enchantment?.Id, actual.Deck[i].Enchantment?.Id,
                $"{label} deck[{i}] enchantment");
            Assert.Equal(expected.Deck[i].Enchantment?.Amount ?? 0, actual.Deck[i].Enchantment?.Amount ?? 0,
                $"{label} deck[{i}] enchantment amount");
        }

        Assert.Equal(expected.Relics.Count, actual.Relics.Count, label + " relic count");
        for (int i = 0; i < expected.Relics.Count; i++)
        {
            Assert.Equal(expected.Relics[i].Id, actual.Relics[i].Id, $"{label} relic[{i}] id");
            AssertPropsEquivalent(expected.Relics[i].Props, actual.Relics[i].Props, $"{label} relic[{i}] props");
        }
        AssertPropsEquivalent(expected.Deck[1].Props, actual.Deck[1].Props, label + " card props");

        Assert.Equal(expected.Potions.Count, actual.Potions.Count, label + " potion count");
        for (int i = 0; i < expected.Potions.Count; i++)
        {
            Assert.Equal(expected.Potions[i].Id, actual.Potions[i].Id, $"{label} potion[{i}] id");
            Assert.Equal(expected.Potions[i].SlotIndex, actual.Potions[i].SlotIndex, $"{label} potion[{i}] slot");
        }
    }

    private static void AssertPropsEquivalent(SavedProperties? expected, SavedProperties? actual, string label)
    {
        Assert.Equal(expected == null, actual == null, label + " presence");
        if (expected == null || actual == null)
        {
            return;
        }

        AssertPropListEquivalent(expected.ints, actual.ints, label + " ints");
        AssertPropListEquivalent(expected.bools, actual.bools, label + " bools");
        AssertPropListEquivalent(expected.strings, actual.strings, label + " strings");
    }

    private static void AssertPropListEquivalent<T>(
        List<SavedProperties.SavedProperty<T>>? expected,
        List<SavedProperties.SavedProperty<T>>? actual,
        string label)
    {
        Assert.Equal(expected?.Count ?? 0, actual?.Count ?? 0, label + " count");
        for (int i = 0; i < (expected?.Count ?? 0); i++)
        {
            Assert.Equal(expected![i].name, actual![i].name, $"{label}[{i}] name");
            Assert.Equal(expected[i].value, actual[i].value, $"{label}[{i}] value");
        }
    }
}
