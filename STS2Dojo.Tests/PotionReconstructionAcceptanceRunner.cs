using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Dojo.STS2DojoCode.Reconstruction;

internal sealed record PotionReconstructionFixture(
    string Name,
    string RunFile,
    int GlobalFloor,
    string[] ExpectedPotions,
    int ExpectedMaxPotionSlots,
    string[]? RequiredRelics = null);

/// <summary>
/// Potion reconstruction acceptance tests (see CLAUDE.md §5/§5d). Covers structural replay
/// (potion_choices/potion_used/potion_discarded) plus max-potion-slot tracking (Ascension's Tight Belt,
/// Potion Belt/Alchemical Coffer/Phial Holster). Also locks in a real bug found and fixed during
/// development: an earlier version additionally tried to recover potion grants/removals from
/// event_choices[].variables (Drowning Beacon, Potion Courier, Ranwid the Elder, Stone of All Time),
/// keyed on the event's *name* alone. That was wrong — those variables are populated for every one of an
/// event's options at generation time regardless of which the player actually picks, so name-only
/// matching produced false-positive grants/removals whenever a non-potion option was chosen (e.g.
/// Drowning Beacon's Climb, which grants a relic, not a potion). Every genuine potion-touching option
/// across all four events turned out to already be 100% structurally tracked, so that whole fallback was
/// removed — see RunReconstructor's potion-handling comment for the full account.
/// </summary>
internal static class PotionReconstructionAcceptanceRunner
{
    public static void Run()
    {
        PotionReconstructionFixture[] fixtures =
        [
            new(
                Name: "Glowwater event grant (Bottle option) is held entering next combat",
                RunFile: "1773537304.run",
                GlobalFloor: 5,
                ExpectedPotions:
                [
                    "POTION.VULNERABLE_POTION",
                    "POTION.GLOWWATER_POTION"
                ],
                ExpectedMaxPotionSlots: 3),
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
                ExpectedMaxPotionSlots: 3,
                RequiredRelics: ["RELIC.PETRIFIED_TOAD"]),
            new(
                Name: "Potion-Shaped Rock discard is removed before next elite",
                RunFile: "1777731795.run",
                GlobalFloor: 28,
                ExpectedPotions: ["POTION.FIRE_POTION"],
                ExpectedMaxPotionSlots: 4,
                RequiredRelics: ["RELIC.PETRIFIED_TOAD"]),
            new(
                Name: "Potion Courier Grab Potions option is held entering next combat",
                RunFile: "1776407214.run",
                GlobalFloor: 21,
                ExpectedPotions:
                [
                    "POTION.FOUL_POTION",
                    "POTION.FOUL_POTION"
                ],
                ExpectedMaxPotionSlots: 2),
            new(
                Name: "Foul Potions used at merchant and bought potion is not double-counted",
                RunFile: "1778868022.run",
                GlobalFloor: 31,
                ExpectedPotions:
                [
                    "POTION.ENERGY_POTION",
                    "POTION.POWER_POTION"
                ],
                ExpectedMaxPotionSlots: 4),
            new(
                // Regression fixture: floor 13 is a Drowning Beacon event, but the CLIMB option was
                // chosen (grants RELIC.FRESNEL_LENS + HP loss, confirmed via this floor's relic_choices) —
                // NOT the Bottle option that grants Glowwater Potion. The event's "Potion" variable is
                // still present either way (populated for both options' hover text at generation time), so
                // this specifically guards against re-introducing the event-name-only matching bug.
                Name: "Drowning Beacon Climb option must not leak a Glowwater Potion grant",
                RunFile: "1782178743.run",
                GlobalFloor: 14,
                ExpectedPotions:
                [
                    "POTION.STRENGTH_POTION",
                    "POTION.POWER_POTION"
                ],
                ExpectedMaxPotionSlots: 2,
                RequiredRelics: ["RELIC.FRESNEL_LENS"]),
            new(
                // Regression fixture: floor 20's Stone of All Time event chose the PUSH option (HP loss +
                // card enchant), not LIFT (which discards a random held potion) — so the held Skill Potion
                // must survive. The event's "DrinkRandomPotion" variable names it either way.
                Name: "Stone of All Time Push option must not remove a held potion",
                RunFile: "1777590946.run",
                GlobalFloor: 21,
                ExpectedPotions:
                [
                    "POTION.SKILL_POTION",
                    "POTION.SPEED_POTION",
                    "POTION.SWIFT_POTION",
                    "POTION.FORTIFIER"
                ],
                ExpectedMaxPotionSlots: 6),
            new(
                // Regression fixture: floor 23's Potion Courier event chose Ransack, which grants ONE
                // random uncommon-rarity potion via RNG (not a fixed "3 Foul Potions" — that 3 is just
                // flavor-text shared with the unrelated Grab Potions option). Here the reward wasn't
                // capturable in potion_choices at all (both existing slots were already full with Cure-All
                // + Attack Potion), so nothing should be guessed into the inventory. Potion Belt is picked
                // up one floor later (floor 24, +2 slots, reflected in ExpectedMaxPotionSlots) — too late
                // to have made room for the floor-23 offer.
                Name: "Potion Courier Ransack with no room is not guessed",
                RunFile: "1779159270.run",
                GlobalFloor: 25,
                ExpectedPotions:
                [
                    "POTION.CURE_ALL",
                    "POTION.ATTACK_POTION"
                ],
                ExpectedMaxPotionSlots: 4,
                RequiredRelics: ["RELIC.POTION_BELT"])
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

        Assert.Equal(fixture.ExpectedMaxPotionSlots, loadout.MaxPotionSlots, fixture.Name + " max potion slots");

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
