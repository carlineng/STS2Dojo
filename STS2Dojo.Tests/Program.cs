using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Dojo.STS2DojoCode.Reconstruction;

TestRunner.Run(
    new ReconstructorFixture(
        Name: "Known validated floor 25 elite",
        RunFile: "1779595721.run",
        GlobalFloor: 25,
        ExpectedCharacter: "CHARACTER.SILENT",
        ExpectedAscension: 10,
        ExpectedEncounter: "ENCOUNTER.DECIMILLIPEDE_ELITE",
        ExpectedMonsterIds:
        [
            "MONSTER.DECIMILLIPEDE_SEGMENT_FRONT",
            "MONSTER.DECIMILLIPEDE_SEGMENT_MIDDLE",
            "MONSTER.DECIMILLIPEDE_SEGMENT_BACK"
        ],
        ExpectedCurrentHp: 30,
        ExpectedMaxHp: 70,
        ExpectedGold: 55,
        ExpectedDeckCount: 24,
        ExpectedRelicCount: 10,
        RequiredCards: ["CARD.ASCENDERS_BANE", "CARD.BLADE_DANCE"],
        RequiredRelics: ["RELIC.RING_OF_THE_SNAKE", "RELIC.TOUGH_BANDAGES", "RELIC.MINIATURE_TENT"]),
    new ReconstructorFixture(
        Name: "Recent first combat floor",
        RunFile: "1781906039.run",
        GlobalFloor: 2,
        ExpectedCharacter: "CHARACTER.SILENT",
        ExpectedAscension: 10,
        ExpectedEncounter: "ENCOUNTER.SEAPUNK_WEAK",
        ExpectedMonsterIds: ["MONSTER.SEAPUNK"],
        ExpectedCurrentHp: 56,
        ExpectedMaxHp: 70,
        ExpectedGold: 0,
        ExpectedDeckCount: 13,
        ExpectedRelicCount: 2,
        RequiredCards: ["CARD.ASCENDERS_BANE"],
        RequiredRelics: ["RELIC.RING_OF_THE_SNAKE", "RELIC.SILKEN_TRESS"],
        ForbiddenCards: ["CARD.PREDATOR"]),
    new ReconstructorFixture(
        Name: "Target-floor rewards excluded",
        RunFile: "1782434199.run",
        GlobalFloor: 9,
        ExpectedCharacter: "CHARACTER.SILENT",
        ExpectedAscension: 10,
        ExpectedEncounter: "ENCOUNTER.TERROR_EEL_ELITE",
        ExpectedMonsterIds: ["MONSTER.TERROR_EEL"],
        ExpectedCurrentHp: 56,
        ExpectedMaxHp: 70,
        ExpectedGold: 51,
        ExpectedDeckCount: 19,
        ExpectedRelicCount: 2,
        RequiredCards: ["CARD.ASCENDERS_BANE", "CARD.DAGGER_SPRAY", "CARD.LEG_SWEEP"],
        RequiredRelics: ["RELIC.RING_OF_THE_SNAKE", "RELIC.SILKEN_TRESS"],
        ForbiddenCards: ["CARD.BOUNCING_FLASK"],
        ForbiddenRelics: ["RELIC.CENTENNIAL_PUZZLE"]),
    new ReconstructorFixture(
        Name: "Event combat uses room type",
        RunFile: "1782705511.run",
        GlobalFloor: 5,
        ExpectedCharacter: "CHARACTER.SILENT",
        ExpectedAscension: 10,
        ExpectedEncounter: "ENCOUNTER.FUZZY_WURM_CRAWLER_WEAK",
        ExpectedMonsterIds: ["MONSTER.FUZZY_WURM_CRAWLER"],
        ExpectedCurrentHp: 56,
        ExpectedMaxHp: 58,
        ExpectedGold: 117,
        ExpectedDeckCount: 16,
        ExpectedRelicCount: 3,
        RequiredCards: ["CARD.ASCENDERS_BANE", "CARD.SHADOWMELD", "CARD.CLUMSY"],
        RequiredRelics: ["RELIC.RING_OF_THE_SNAKE", "RELIC.LEAFY_POULTICE", "RELIC.NUNCHAKU"],
        ForbiddenCards: ["CARD.DAGGER_SPRAY"]),
    new ReconstructorFixture(
        Name: "Recent full-length boss win",
        RunFile: "1782524433.run",
        GlobalFloor: 48,
        ExpectedCharacter: "CHARACTER.IRONCLAD",
        ExpectedAscension: 10,
        ExpectedEncounter: "ENCOUNTER.TEST_SUBJECT_BOSS",
        ExpectedMonsterIds: ["MONSTER.TEST_SUBJECT"],
        ExpectedCurrentHp: 96,
        ExpectedMaxHp: 96,
        ExpectedGold: 282,
        ExpectedDeckCount: 38,
        ExpectedRelicCount: 19,
        RequiredCards: ["CARD.ASCENDERS_BANE", "CARD.BLOODLETTING", "CARD.WHIRLWIND"],
        RequiredRelics: ["RELIC.BURNING_BLOOD", "RELIC.FESTIVE_POPPER"]),
    new ReconstructorFixture(
        Name: "Late boss with relic removals",
        RunFile: "1782182181.run",
        GlobalFloor: 48,
        ExpectedCharacter: "CHARACTER.SILENT",
        ExpectedAscension: 10,
        ExpectedEncounter: "ENCOUNTER.AEONGLASS_BOSS",
        ExpectedMonsterIds: ["MONSTER.AEONGLASS"],
        ExpectedCurrentHp: 70,
        ExpectedMaxHp: 70,
        ExpectedGold: 256,
        ExpectedDeckCount: 32,
        ExpectedRelicCount: 18,
        RequiredCards: ["CARD.ASCENDERS_BANE", "CARD.MALAISE", "CARD.PHANTOM_BLADES"],
        RequiredRelics: ["RELIC.RING_OF_THE_SNAKE", "RELIC.FESTIVE_POPPER"]),
    new ReconstructorFixture(
        Name: "Mutation-heavy late boss",
        RunFile: "1781843292.run",
        GlobalFloor: 50,
        ExpectedCharacter: "CHARACTER.SILENT",
        ExpectedAscension: 10,
        ExpectedEncounter: "ENCOUNTER.TEST_SUBJECT_BOSS",
        ExpectedMonsterIds: ["MONSTER.TEST_SUBJECT"],
        ExpectedCurrentHp: 65,
        ExpectedMaxHp: 70,
        ExpectedGold: 7,
        ExpectedDeckCount: 34,
        ExpectedRelicCount: 18,
        RequiredCards: ["CARD.ASCENDERS_BANE", "CARD.FOOTWORK", "CARD.PREPARED"],
        RequiredRelics: ["RELIC.RING_OF_THE_SNAKE", "RELIC.BAG_OF_PREPARATION"]),
    new ReconstructorFixture(
        Name: "Defect A10 final boss",
        RunFile: "1782696823.run",
        GlobalFloor: 49,
        ExpectedCharacter: "CHARACTER.DEFECT",
        ExpectedAscension: 10,
        ExpectedEncounter: "ENCOUNTER.AEONGLASS_BOSS",
        ExpectedMonsterIds: ["MONSTER.AEONGLASS"],
        ExpectedCurrentHp: 49,
        ExpectedMaxHp: 77,
        ExpectedGold: 331,
        ExpectedDeckCount: 34,
        ExpectedRelicCount: 19,
        RequiredCards: ["CARD.ASCENDERS_BANE", "CARD.CLAW", "CARD.GLACIER"],
        RequiredRelics: ["RELIC.CRACKED_CORE", "RELIC.WAR_PAINT"]));

internal sealed record ReconstructorFixture(
    string Name,
    string RunFile,
    int GlobalFloor,
    string ExpectedCharacter,
    int ExpectedAscension,
    string ExpectedEncounter,
    string[] ExpectedMonsterIds,
    int ExpectedCurrentHp,
    int ExpectedMaxHp,
    int ExpectedGold,
    int ExpectedDeckCount,
    int ExpectedRelicCount,
    string[]? RequiredCards = null,
    string[]? RequiredRelics = null,
    string[]? ForbiddenCards = null,
    string[]? ForbiddenRelics = null);

internal static class TestRunner
{
    public static void Run(params ReconstructorFixture[] fixtures)
    {
        int passed = 0;
        foreach (ReconstructorFixture fixture in fixtures)
        {
            RunFixture(fixture);
            passed++;
            Console.WriteLine("PASS " + fixture.Name);
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} RunReconstructor fixture tests passed.");
    }

    private static void RunFixture(ReconstructorFixture fixture)
    {
        string runPath = Path.Combine(FindRepoRoot(), "runfiles", fixture.RunFile);
        RunHistory run = TestRunHistoryLoader.Load(runPath);
        StartingLoadout starting = StartingLoadout.ForCharacter(run.Players.Single().Character.ToString(), run.Ascension);

        ReconstructedLoadout loadout = RunReconstructor.Reconstruct(
            run,
            fixture.GlobalFloor,
            starting.Deck,
            starting.Relics,
            starting.Hp,
            starting.Gold);

        Assert.Equal(fixture.ExpectedCharacter, loadout.CharacterId.ToString(), fixture.Name + " character");
        Assert.Equal(fixture.ExpectedAscension, loadout.Ascension, fixture.Name + " ascension");
        Assert.Equal(fixture.ExpectedEncounter, loadout.EncounterId.ToString(), fixture.Name + " encounter");
        Assert.SequenceEqual(fixture.ExpectedMonsterIds, loadout.MonsterIds.Select(id => id.ToString()).ToArray(),
            fixture.Name + " monster ids");
        Assert.Equal(fixture.ExpectedCurrentHp, loadout.CurrentHp, fixture.Name + " current hp");
        Assert.Equal(fixture.ExpectedMaxHp, loadout.MaxHp, fixture.Name + " max hp");
        Assert.Equal(fixture.ExpectedGold, loadout.Gold, fixture.Name + " gold");
        Assert.Equal(fixture.ExpectedDeckCount, loadout.Deck.Count, fixture.Name + " deck count");
        Assert.Equal(fixture.ExpectedRelicCount, loadout.Relics.Count, fixture.Name + " relic count");

        AssertContainsAll(loadout.Deck.Select(c => c.Card.Id?.ToString()), fixture.RequiredCards, fixture.Name + " deck");
        AssertContainsAll(loadout.Relics.Select(r => r.Relic.Id?.ToString()), fixture.RequiredRelics, fixture.Name + " relics");
        AssertContainsNone(loadout.Deck.Select(c => c.Card.Id?.ToString()), fixture.ForbiddenCards, fixture.Name + " deck");
        AssertContainsNone(loadout.Relics.Select(r => r.Relic.Id?.ToString()), fixture.ForbiddenRelics, fixture.Name + " relics");

        Assert.True(loadout.Deck.Any(c => c.Provenance == Provenance.Derived), fixture.Name + " has derived cards");
        if (fixture.GlobalFloor > 2)
        {
            Assert.True(loadout.Deck.Any(c => c.Provenance == Provenance.Replayed), fixture.Name + " has replayed cards");
        }
    }

    private static void AssertContainsAll(IEnumerable<string?> actual, string[]? expected, string label)
    {
        if (expected == null)
        {
            return;
        }

        HashSet<string> actualSet = actual.Where(id => id != null).Cast<string>().ToHashSet();
        foreach (string id in expected)
        {
            Assert.True(actualSet.Contains(id), label + " should contain " + id);
        }
    }

    private static void AssertContainsNone(IEnumerable<string?> actual, string[]? forbidden, string label)
    {
        if (forbidden == null)
        {
            return;
        }

        HashSet<string> actualSet = actual.Where(id => id != null).Cast<string>().ToHashSet();
        foreach (string id in forbidden)
        {
            Assert.True(!actualSet.Contains(id), label + " should not contain " + id);
        }
    }

    public static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "runfiles");
            if (Directory.Exists(candidate))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find repo root containing runfiles/.");
    }
}

internal sealed record StartingLoadout(
    IReadOnlyList<SerializableCard> Deck,
    IReadOnlyList<SerializableRelic> Relics,
    int Hp,
    int Gold)
{
    public static StartingLoadout ForCharacter(string characterId, int ascension)
    {
        bool hasAscendersBane = ascension >= 10;
        return characterId switch
        {
            "CHARACTER.SILENT" => new StartingLoadout(
                StartingDeck([
                    "CARD.STRIKE_SILENT", "CARD.STRIKE_SILENT", "CARD.STRIKE_SILENT", "CARD.STRIKE_SILENT",
                    "CARD.STRIKE_SILENT", "CARD.DEFEND_SILENT", "CARD.DEFEND_SILENT", "CARD.DEFEND_SILENT",
                    "CARD.DEFEND_SILENT", "CARD.DEFEND_SILENT", "CARD.NEUTRALIZE", "CARD.SURVIVOR"
                ], hasAscendersBane),
                StartingRelics(["RELIC.RING_OF_THE_SNAKE"]),
                Hp: 70,
                Gold: 99),
            "CHARACTER.IRONCLAD" => new StartingLoadout(
                StartingDeck([
                    "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD",
                    "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.DEFEND_IRONCLAD",
                    "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.BASH"
                ], hasAscendersBane),
                StartingRelics(["RELIC.BURNING_BLOOD"]),
                Hp: 80,
                Gold: 99),
            "CHARACTER.DEFECT" => new StartingLoadout(
                StartingDeck([
                    "CARD.STRIKE_DEFECT", "CARD.STRIKE_DEFECT", "CARD.STRIKE_DEFECT", "CARD.STRIKE_DEFECT",
                    "CARD.DEFEND_DEFECT", "CARD.DEFEND_DEFECT", "CARD.DEFEND_DEFECT", "CARD.DEFEND_DEFECT",
                    "CARD.ZAP", "CARD.DUALCAST"
                ], hasAscendersBane),
                StartingRelics(["RELIC.CRACKED_CORE"]),
                Hp: 75,
                Gold: 99),
            _ => throw new NotSupportedException("No test starting loadout for " + characterId)
        };
    }

    private static IReadOnlyList<SerializableCard> StartingDeck(string[] ids, bool hasAscendersBane)
    {
        List<SerializableCard> cards = ids.Select(id => new SerializableCard
        {
            Id = ModelId.Deserialize(id),
            FloorAddedToDeck = 1
        }).ToList();

        if (hasAscendersBane)
        {
            cards.Add(new SerializableCard
            {
                Id = ModelId.Deserialize("CARD.ASCENDERS_BANE"),
                FloorAddedToDeck = 1
            });
        }

        return cards;
    }

    private static IReadOnlyList<SerializableRelic> StartingRelics(string[] ids) =>
        ids.Select(id => new SerializableRelic
        {
            Id = ModelId.Deserialize(id),
            FloorAddedToDeck = 1
        }).ToList();
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailureException($"{label}: expected {expected}, got {actual}");
        }
    }

    public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string label)
    {
        if (expected.Count != actual.Count)
        {
            throw new TestFailureException($"{label}: expected {expected.Count} items, got {actual.Count}");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
            {
                throw new TestFailureException($"{label}[{i}]: expected {expected[i]}, got {actual[i]}");
            }
        }
    }

    public static void True(bool condition, string label)
    {
        if (!condition)
        {
            throw new TestFailureException(label);
        }
    }
}

internal sealed class TestFailureException(string message) : Exception(message);

internal static class TestRunHistoryLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static RunHistory Load(string path) =>
        JsonSerializer.Deserialize<RunHistory>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException("Failed to deserialize " + path);
}
