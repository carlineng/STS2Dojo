using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using STS2Dojo.STS2DojoCode.Reconstruction;

/// <summary>
/// Fixture tests for the custom Dojo run browser's pure logic: per-run summaries, per-act boss/elite
/// extraction (incl. death-fight marking and the ascension-10 two-boss final act), and the sidebar
/// filter/sort/search queries. Display names go through a fake resolver, mirroring how the in-game
/// screen wraps CharacterModel.Title/EncounterModel.Title.
/// </summary>
internal static class DojoRunBrowserLogicRunner
{
    private static readonly Dictionary<string, string> DisplayNames = new()
    {
        ["CHARACTER.SILENT"] = "Silent",
        ["CHARACTER.IRONCLAD"] = "Ironclad",
        ["CHARACTER.DEFECT"] = "Defect",
        ["ENCOUNTER.QUEEN_BOSS"] = "The Queen",
        ["ENCOUNTER.AEONGLASS_BOSS"] = "Aeon Glass",
        ["ENCOUNTER.TERROR_EEL_ELITE"] = "Terror Eel",
        ["ENCOUNTER.DECIMILLIPEDE_ELITE"] = "Decimillipede",
        ["RELIC.GREMLIN_HORN"] = "Gremlin Horn",
        ["CARD.FOOTWORK"] = "Footwork"
    };

    private static string ResolveName(ModelId id) =>
        DisplayNames.TryGetValue(id.ToString(), out string? name) ? name : id.Entry;

    public static void Run()
    {
        Run("run summary fields", RunSummaryFields);
        Run("per-act boss/elite extraction", PerActExtraction);
        Run("death-fight marking", DeathFightMarking);
        Run("filtering", Filtering);
        Run("search", Search);
        Run("sorting", Sorting);

        Console.WriteLine();
        Console.WriteLine("6 Dojo run-browser logic test groups passed.");
    }

    private static void Run(string name, Action test)
    {
        test();
        Console.WriteLine("PASS " + name);
    }

    private static void RunSummaryFields()
    {
        DojoRunSummary win = Summarize("1782524433.run");
        Assert.Equal("CHARACTER.IRONCLAD", win.CharacterId.ToString(), "character");
        Assert.Equal(10, win.Ascension, "ascension");
        Assert.True(win.Win && !win.WasAbandoned, "win flags");
        Assert.Equal(49, win.FloorsReached, "floors reached");
        Assert.Equal(69, win.EndHp, "end hp");
        Assert.Equal(96, win.EndMaxHp, "end max hp");
        Assert.Equal("RRFV94VJEW", win.Seed, "seed");
        Assert.Equal(1782524433L, win.StartTime, "start time");
        Assert.Equal(2955f, win.RunTimeSeconds, "run time");
        Assert.Equal(38, win.DeckCount, "deck count");
        Assert.Equal(19, win.RelicCount, "relic count");
        Assert.Equal(38, win.DeckCardIds.Count, "deck card ids cover the whole deck");
        Assert.True(win.DeckCardIds.Any(id => id.ToString() == "CARD.WHIRLWIND"), "deck card ids content");

        DojoRunSummary loss = Summarize("1779595721.run");
        Assert.True(!loss.Win, "loss flag");
        Assert.Equal(25, loss.FloorsReached, "loss floors reached");
        Assert.Equal(0, loss.EndHp, "loss end hp");
        Assert.Equal(70, loss.EndMaxHp, "loss end max hp");
    }

    private static void PerActExtraction()
    {
        // Lost run that ended mid-act-2: only the two visited acts appear, with act ids from the run file.
        DojoRunSummary loss = Summarize("1779595721.run");
        Assert.Equal(2, loss.Acts.Count, "visited acts only");
        DojoActSummary act1 = loss.Acts[0];
        Assert.Equal("ACT.UNDERDOCKS", act1.ActId?.ToString(), "act 1 id");
        Assert.SequenceEqual(
            ["ENCOUNTER.SKULKING_COLONY_ELITE@12", "ENCOUNTER.TERROR_EEL_ELITE@14"],
            act1.Elites.Select(f => $"{f.EncounterId}@{f.GlobalFloor}").ToArray(),
            "act 1 elites");
        Assert.SequenceEqual(
            ["ENCOUNTER.SOUL_FYSH_BOSS@17"],
            act1.Bosses.Select(f => $"{f.EncounterId}@{f.GlobalFloor}").ToArray(),
            "act 1 boss");
        DojoActSummary act2 = loss.Acts[1];
        Assert.Equal("ACT.HIVE", act2.ActId?.ToString(), "act 2 id");
        Assert.Equal(0, act2.Bosses.Count, "act 2 has no reached boss");
        Assert.SequenceEqual(
            ["ENCOUNTER.DECIMILLIPEDE_ELITE@25"],
            act2.Elites.Select(f => $"{f.EncounterId}@{f.GlobalFloor}").ToArray(),
            "act 2 elites");

        // Ascension-10 victory: the final act contains two boss fights, no special casing.
        DojoRunSummary win = Summarize("1782524433.run");
        Assert.Equal(3, win.Acts.Count, "full run act count");
        Assert.Equal("ACT.GLORY", win.Acts[2].ActId?.ToString(), "final act id");
        Assert.SequenceEqual(
            ["ENCOUNTER.TEST_SUBJECT_BOSS@48", "ENCOUNTER.QUEEN_BOSS@49"],
            win.Acts[2].Bosses.Select(f => $"{f.EncounterId}@{f.GlobalFloor}").ToArray(),
            "A10 win has two final-act bosses");
    }

    private static void DeathFightMarking()
    {
        // Died to an elite mid-act: exactly that fight is the death fight.
        DojoRunSummary eliteDeath = Summarize("1779595721.run");
        List<DojoFightSummary> allFights = eliteDeath.Acts.SelectMany(a => a.DisplayFights).ToList();
        Assert.SequenceEqual(
            ["ENCOUNTER.DECIMILLIPEDE_ELITE@25"],
            allFights.Where(f => f.WasDeathFight).Select(f => $"{f.EncounterId}@{f.GlobalFloor}").ToArray(),
            "elite death fight");

        // Died to the second final-act boss: floor 48's cleared boss must NOT be marked.
        DojoRunSummary bossDeath = Summarize("1782696823.run");
        Assert.SequenceEqual(
            ["ENCOUNTER.AEONGLASS_BOSS@49"],
            bossDeath.Acts.SelectMany(a => a.Bosses).Where(f => f.WasDeathFight)
                .Select(f => $"{f.EncounterId}@{f.GlobalFloor}").ToArray(),
            "boss death fight");

        // Died to a normal hallway fight: surface that final fight even though normal fights are
        // otherwise omitted from compact run rows.
        ModelId normalEncounter = ModelId.Deserialize("ENCOUNTER.SLIMES_NORMAL");
        ModelId normalMonster = ModelId.Deserialize("MONSTER.SLIMED_BERSERKER");
        RunHistory normalDeath = new()
        {
            Win = false,
            WasAbandoned = false,
            Seed = "NORMALDEATH",
            KilledByEncounter = normalEncounter,
            Acts = [ModelId.Deserialize("ACT.UNDERDOCKS")],
            Players =
            [
                new RunHistoryPlayer
                {
                    Id = 1,
                    Character = ModelId.Deserialize("CHARACTER.IRONCLAD")
                }
            ],
            MapPointHistory =
            [
                [
                    new MapPointHistoryEntry
                    {
                        Rooms =
                        [
                            new MapPointRoomHistoryEntry
                            {
                                RoomType = RoomType.Monster,
                                ModelId = normalEncounter,
                                MonsterIds = [normalMonster]
                            }
                        ],
                        PlayerStats =
                        [
                            new PlayerMapPointHistoryEntry
                            {
                                PlayerId = 1,
                                CurrentHp = 0,
                                MaxHp = 80
                            }
                        ]
                    }
                ]
            ]
        };
        DojoRunSummary normalDeathSummary = DojoRunSummarizer.Summarize("normal-death.run", normalDeath);
        Assert.SequenceEqual(
            ["ENCOUNTER.SLIMES_NORMAL@1"],
            normalDeathSummary.Acts[0].OtherDeathFights.Select(f => $"{f.EncounterId}@{f.GlobalFloor}").ToArray(),
            "normal death fight is displayed");
        Assert.Equal(RoomType.Monster, normalDeathSummary.Acts[0].OtherDeathFights[0].RoomType,
            "normal death fight room type");
        Assert.Equal(normalMonster, normalDeathSummary.Acts[0].OtherDeathFights[0].DisplayId,
            "normal death fight display id");
        Assert.Equal(normalEncounter, normalDeathSummary.Acts[0].OtherDeathFights[0].EncounterId,
            "normal death fight replay encounter id");
        Assert.True(normalDeathSummary.Acts[0].OtherDeathFights[0].WasDeathFight,
            "normal death fight marked fatal");

        // A victory has no death fight.
        DojoRunSummary win = Summarize("1782524433.run");
        Assert.True(win.Acts.SelectMany(a => a.DisplayFights).All(f => !f.WasDeathFight),
            "no death fight on a win");

        // An abandoned run has no death fight even if its last floor was a combat.
        RunHistory abandoned = TestRunHistoryLoader.Load(RunPath("1779595721.run"));
        RunHistory abandonedCopy = new()
        {
            SchemaVersion = abandoned.SchemaVersion,
            Ascension = abandoned.Ascension,
            Win = false,
            WasAbandoned = true,
            Seed = abandoned.Seed,
            StartTime = abandoned.StartTime,
            RunTime = abandoned.RunTime,
            KilledByEncounter = abandoned.KilledByEncounter,
            Acts = abandoned.Acts,
            Players = abandoned.Players,
            MapPointHistory = abandoned.MapPointHistory
        };
        DojoRunSummary abandonedSummary = DojoRunSummarizer.Summarize("abandoned.run", abandonedCopy);
        Assert.True(
            abandonedSummary.Acts.SelectMany(a => a.DisplayFights).All(f => !f.WasDeathFight),
            "no death fight on an abandoned run");
    }

    private static void Filtering()
    {
        List<DojoRunSummary> runs = AllRuns();

        List<DojoRunSummary> ironclad = DojoRunListQueries.Apply(
            runs, new DojoRunFilter(Character: ModelId.Deserialize("CHARACTER.IRONCLAD")),
            DojoRunSortOrder.Newest, ResolveName);
        Assert.SequenceEqual(["1782524433.run"], FileNames(ironclad), "character filter");

        List<DojoRunSummary> ascensionMatch = DojoRunListQueries.Apply(
            runs, new DojoRunFilter(Ascension: 10), DojoRunSortOrder.Newest, ResolveName);
        Assert.Equal(3, ascensionMatch.Count, "ascension exact match");
        List<DojoRunSummary> ascensionMiss = DojoRunListQueries.Apply(
            runs, new DojoRunFilter(Ascension: 5), DojoRunSortOrder.Newest, ResolveName);
        Assert.Equal(0, ascensionMiss.Count, "ascension mismatch excludes all");

        List<DojoRunSummary> victories = DojoRunListQueries.Apply(
            runs, new DojoRunFilter(Victory: DojoVictoryFilter.Victory), DojoRunSortOrder.Newest, ResolveName);
        Assert.SequenceEqual(["1782524433.run"], FileNames(victories), "victory filter");
        List<DojoRunSummary> defeats = DojoRunListQueries.Apply(
            runs, new DojoRunFilter(Victory: DojoVictoryFilter.Defeat), DojoRunSortOrder.Newest, ResolveName);
        Assert.SequenceEqual(["1782696823.run", "1779595721.run"], FileNames(defeats), "defeat filter");

        List<DojoRunSummary> combined = DojoRunListQueries.Apply(
            runs,
            new DojoRunFilter(
                Character: ModelId.Deserialize("CHARACTER.DEFECT"),
                Ascension: 10,
                Victory: DojoVictoryFilter.Defeat),
            DojoRunSortOrder.Newest, ResolveName);
        Assert.SequenceEqual(["1782696823.run"], FileNames(combined), "combined filters");
    }

    private static void Search()
    {
        List<DojoRunSummary> runs = AllRuns();

        Assert.SequenceEqual(["1782524433.run"],
            FileNames(Apply(runs, new DojoRunFilter(SearchText: "RRFV94"))), "seed search");
        Assert.SequenceEqual(["1782524433.run"],
            FileNames(Apply(runs, new DojoRunFilter(SearchText: "queen"))), "boss display-name search");
        Assert.SequenceEqual(["1782696823.run"],
            FileNames(Apply(runs, new DojoRunFilter(SearchText: "Defect"))), "character name search");
        // Both losses fought a Terror Eel elite; the win (Overgrowth act 1) did not.
        Assert.SequenceEqual(["1782696823.run", "1779595721.run"],
            FileNames(Apply(runs, new DojoRunFilter(SearchText: "terror eel"))), "elite display-name search");
        Assert.SequenceEqual(["1782696823.run"],
            FileNames(Apply(runs, new DojoRunFilter(SearchText: "AEONGLASS_BOSS"))), "raw encounter-id search");
        // Only 1779595721.run's ending relics include Gremlin Horn.
        Assert.SequenceEqual(["1779595721.run"],
            FileNames(Apply(runs, new DojoRunFilter(SearchText: "gremlin"))), "relic display-name search");
        Assert.SequenceEqual(["1779595721.run"],
            FileNames(Apply(runs, new DojoRunFilter(SearchText: "RELIC.GREMLIN_HORN"))), "raw relic-id search");
        // Deck-card search: Footwork ended only the Silent loss's deck; Whirlwind only the Ironclad win's.
        Assert.SequenceEqual(["1779595721.run"],
            FileNames(Apply(runs, new DojoRunFilter(SearchText: "footwork"))), "card display-name search");
        Assert.SequenceEqual(["1782524433.run"],
            FileNames(Apply(runs, new DojoRunFilter(SearchText: "CARD.WHIRLWIND"))), "raw card-id search");
        // All three fixtures are ascension-10 runs, so every final deck carries Ascender's Bane.
        Assert.Equal(3, Apply(runs, new DojoRunFilter(SearchText: "ASCENDERS_BANE")).Count,
            "card carried by every deck");
        Assert.Equal(0, Apply(runs, new DojoRunFilter(SearchText: "zzz-no-match")).Count, "no-match search");
        Assert.Equal(3, Apply(runs, new DojoRunFilter(SearchText: "   ")).Count, "blank search matches all");
    }

    private static void Sorting()
    {
        List<DojoRunSummary> runs = AllRuns();

        Assert.SequenceEqual(["1782696823.run", "1782524433.run", "1779595721.run"],
            FileNames(Apply(runs, DojoRunFilter.None, DojoRunSortOrder.Newest)), "newest first");
        Assert.SequenceEqual(["1779595721.run", "1782524433.run", "1782696823.run"],
            FileNames(Apply(runs, DojoRunFilter.None, DojoRunSortOrder.Oldest)), "oldest first");
        // Both 49-floor runs tie on floor; newest of the two wins the tie.
        Assert.SequenceEqual(["1782696823.run", "1782524433.run", "1779595721.run"],
            FileNames(Apply(runs, DojoRunFilter.None, DojoRunSortOrder.Floor)), "deepest floor first");
        // All ascension 10: falls back to newest-first ordering.
        Assert.SequenceEqual(["1782696823.run", "1782524433.run", "1779595721.run"],
            FileNames(Apply(runs, DojoRunFilter.None, DojoRunSortOrder.Ascension)), "ascension ties by newest");
    }

    private static List<DojoRunSummary> Apply(
        List<DojoRunSummary> runs, DojoRunFilter filter, DojoRunSortOrder sort = DojoRunSortOrder.Newest) =>
        DojoRunListQueries.Apply(runs, filter, sort, ResolveName);

    private static string[] FileNames(IEnumerable<DojoRunSummary> runs) =>
        runs.Select(r => Path.GetFileName(r.FilePath)).ToArray();

    private static List<DojoRunSummary> AllRuns() =>
        [Summarize("1779595721.run"), Summarize("1782524433.run"), Summarize("1782696823.run")];

    private static DojoRunSummary Summarize(string runFile) =>
        DojoRunSummarizer.Summarize(RunPath(runFile), TestRunHistoryLoader.Load(RunPath(runFile)));

    private static string RunPath(string runFile) =>
        Path.Combine(TestRunner.FindRepoRoot(), "runfiles", runFile);
}
