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

/// <summary>
/// Potion reconstruction acceptance tests (see CLAUDE.md §5/§9/§10). Covers both the structural replay
/// path (potion_choices/potion_used/potion_discarded) and the event-only grant/removal path (Drowning
/// Beacon, Potion Courier, Ranwid the Elder, Stone of All Time — see RunReconstructor's per-floor event
/// handling and PotionNameResolver.cs), each fixture hand-traced against the actual run file.
/// </summary>
internal static class PotionReconstructionAcceptanceRunner
{
    public static void Run()
    {
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
                // Corrected from the original placeholder's ["ENERGY_POTION","POWER_POTION"]: that
                // predated any implementation and missed floor 3's untracked Drowning Beacon Glowwater
                // Potion grant (verified: no potion_used/potion_discarded/final-snapshot entry for it
                // anywhere in this file — it's a genuine still-held potion, not a bug).
                Name: "Foul Potions used at merchant and bought potion is not double-counted",
                RunFile: "1778868022.run",
                GlobalFloor: 31,
                ExpectedPotions:
                [
                    "POTION.GLOWWATER_POTION",
                    "POTION.ENERGY_POTION",
                    "POTION.POWER_POTION"
                ]),
            new(
                Name: "Drowning Beacon event-only grant resolved by display name",
                RunFile: "1782178743.run",
                GlobalFloor: 14,
                ExpectedPotions:
                [
                    "POTION.STRENGTH_POTION",
                    "POTION.POWER_POTION",
                    "POTION.GLOWWATER_POTION"
                ]),
            new(
                Name: "Potion Courier Ransack grants Foul Potions with zero structural trace",
                RunFile: "1779159270.run",
                GlobalFloor: 25,
                ExpectedPotions:
                [
                    "POTION.CURE_ALL",
                    "POTION.ATTACK_POTION",
                    "POTION.FOUL_POTION",
                    "POTION.FOUL_POTION",
                    "POTION.FOUL_POTION"
                ]),
            new(
                Name: "Stone of All Time removes a held potion with zero structural trace",
                RunFile: "1777590946.run",
                GlobalFloor: 21,
                ExpectedPotions:
                [
                    "POTION.SPEED_POTION",
                    "POTION.SWIFT_POTION",
                    "POTION.FORTIFIER"
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
