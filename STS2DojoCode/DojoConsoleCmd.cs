using System;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Exceptions;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Spike command for the STS2 Dojo (see CLAUDE.md §8). Proves the Approach-A launch sequence end-to-end in the
/// live game: stand up a throwaway NON-SAVING run and drop straight into a chosen encounter, bypassing the map.
///
/// Usage in the dev console:  <c>dojo &lt;encounter_id&gt;</c>
/// (encounter ids are the same ones the built-in <c>fight</c> command autocompletes.)
///
/// This is the empirical half of the spike:
///   Q1 (launch from a controlled context) — the run is synthetic; no real map graph or serialized player needed.
///   Q2 (persists nothing) — <c>shouldSave:false</c> is the save kill-switch; the save dir should be byte-identical
///       after both a win and a loss.
///   Q3 (clean return) — win/lose flow back through the game's normal menus.
/// The hardcoded junk loadout (gold = 999) is a stand-in for the future reconstructor output.
/// </summary>
public class DojoConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "dojo";

    public override string Args => "<encounter_id:string>";

    public override string Description =>
        "STS2 Dojo spike: start a throwaway NON-SAVING run and jump straight into the given encounter.";

    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length == 0)
        {
            return new CmdResult(success: false, "Usage: dojo <encounter_id>  (try the ids that 'fight' autocompletes)");
        }

        ModelId encounterId = new(ModelId.SlugifyCategory<EncounterModel>(), args[0].ToUpperInvariant());
        EncounterModel encounter;
        try
        {
            encounter = ModelDb.GetById<EncounterModel>(encounterId).ToMutable();
        }
        catch (ModelNotFoundException)
        {
            return new CmdResult(success: false, "Encounter '" + encounterId.Entry + "' not found.");
        }

        Task task = LaunchDojoFight(encounter.Id);
        return new CmdResult(task, success: true,
            "Dojo: throwaway non-saving run -> '" + encounter.Id.Entry + "'. Save dir should be byte-identical after win OR loss.");
    }

    private static async Task LaunchDojoFight(ModelId encounterId)
    {
        try
        {
            NGame? game = NGame.Instance;
            if (game == null)
            {
                MainFile.Logger.Error("[STS2Dojo] NGame.Instance is null; cannot launch Dojo fight.");
                return;
            }

            // Hardcoded junk context — dojoreplay (STS2DojoCode/DojoReplayConsoleCmd.cs) uses the real
            // reconstructor instead. Skip RandomCharacter.
            CharacterModel character = ModelDb.AllCharacters.First(c => c.GetType().Name != "RandomCharacter");

            MainFile.Logger.Info(
                "[STS2Dojo] Launching '" + encounterId.Entry + "' (shouldSave=false, character=" +
                character.Id.Entry + ", gold=999).");

            // §8.0 launch sequence. shouldSave:false is the persistence kill-switch (Q2). The mutate
            // callback runs before the run's scene is created — see DojoLaunch.cs's class docs.
            await DojoLaunch.LaunchThrowawayRun(game, character, ascensionLevel: 0, encounterId, mutate: runState =>
            {
                runState.Players[0].Gold = 999;
            });
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Dojo launch failed: " + e);
        }
    }
}
