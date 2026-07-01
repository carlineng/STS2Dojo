using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Dojo.STS2DojoCode.Reconstruction;

internal sealed record PotionReconstructionFixture(
    string Name,
    string RunFile,
    int GlobalFloor,
    string[] ExpectedPotions,
    string[]? RequiredRelics = null);

internal static class PotionReconstructionAcceptanceRunner
{
    private const string OptInEnvVar = "STS2DOJO_RUN_POTION_ACCEPTANCE";

    public static void RunIfRequested()
    {
        if (Environment.GetEnvironmentVariable(OptInEnvVar) != "1")
        {
            Console.WriteLine();
            Console.WriteLine(
                $"SKIP potion reconstruction acceptance tests (set {OptInEnvVar}=1 to run; expected to fail until potion replay is implemented).");
            return;
        }

        PotionReconstructionFixture[] fixtures =
        [
            new(
                Name: "Glowwater event grant is held entering next combat",
                RunFile: "1773537304.run",
                GlobalFloor: 5,
                ExpectedPotions:
                [
                    "POTION.VULNERABLE_POTION",
                    "POTION.GLOWWATER_POTION"
                ]),
            new(
                Name: "Petrified Toad can leave Potion-Shaped Rock in inventory",
                RunFile: "1773538354.run",
                GlobalFloor: 17,
                ExpectedPotions:
                [
                    "POTION.DUPLICATOR",
                    "POTION.GLOWWATER_POTION",
                    "POTION.POTION_SHAPED_ROCK"
                ],
                RequiredRelics: ["RELIC.PETRIFIED_TOAD"]),
            new(
                Name: "Potion-Shaped Rock discard is removed before next elite",
                RunFile: "1777731795.run",
                GlobalFloor: 28,
                ExpectedPotions: ["POTION.FIRE_POTION"],
                RequiredRelics: ["RELIC.PETRIFIED_TOAD"]),
            new(
                Name: "Potion Courier Foul Potions are held entering next combat",
                RunFile: "1776407214.run",
                GlobalFloor: 21,
                ExpectedPotions:
                [
                    "POTION.FOUL_POTION",
                    "POTION.FOUL_POTION"
                ]),
            new(
                Name: "Foul Potions used at merchant and bought potion is not double-counted",
                RunFile: "1778868022.run",
                GlobalFloor: 31,
                ExpectedPotions:
                [
                    "POTION.ENERGY_POTION",
                    "POTION.POWER_POTION"
                ])
        ];

        int passed = 0;
        foreach (PotionReconstructionFixture fixture in fixtures)
        {
            RunFixture(fixture);
            passed++;
            Console.WriteLine("PASS " + fixture.Name);
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} potion reconstruction acceptance tests passed.");
    }

    private static void RunFixture(PotionReconstructionFixture fixture)
    {
        RunHistory run = TestRunHistoryLoader.Load(
            Path.Combine(TestRunner.FindRepoRoot(), "runfiles", fixture.RunFile));
        StartingLoadout starting = StartingLoadout.ForCharacter(run.Players.Single().Character.ToString(), run.Ascension);

        ReconstructedLoadout loadout = RunReconstructor.Reconstruct(
            run,
            fixture.GlobalFloor,
            starting.Deck,
            starting.Relics,
            starting.Hp,
            starting.Gold);

        Assert.SequenceEqual(
            fixture.ExpectedPotions,
            loadout.Potions.Select(p => p.PotionId.ToString()).ToArray(),
            fixture.Name + " potions");

        if (fixture.RequiredRelics != null)
        {
            HashSet<string> relics = loadout.Relics
                .Select(r => r.Relic.Id?.ToString())
                .Where(id => id != null)
                .Cast<string>()
                .ToHashSet();
            foreach (string relicId in fixture.RequiredRelics)
            {
                Assert.True(relics.Contains(relicId), fixture.Name + " should contain " + relicId);
            }
        }

        Assert.True(loadout.Potions.All(p => p.Provenance == Provenance.Replayed),
            fixture.Name + " potions should be replayed, not assumed");
    }
}
