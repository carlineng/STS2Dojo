using System;
using System.Collections.Generic;
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
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Dojo.STS2DojoCode.Reconstruction;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Reconstructor integration test (see CLAUDE.md §9 roadmap item 1). Loads a real <c>.run</c> history
/// file from an absolute path, reconstructs the loadout entering the fight on a given global floor
/// (RunReconstructor.cs), and launches it through the same §8.0 throwaway-run sequence as <c>dojo</c> —
/// replacing the hardcoded junk loadout with real reconstructed deck/relics/hp/gold.
///
/// Usage in the dev console:  <c>dojoreplay &lt;absolute_run_file_path&gt; &lt;global_floor:int&gt;</c>
/// </summary>
public class DojoReplayConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "dojoreplay";

    public override string Args => "<run_file_path:string> <floor:int>";

    public override string Description =>
        "STS2 Dojo: reconstruct the loadout entering a fight from a real .run file and launch it (non-saving).";

    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length < 2)
        {
            return new CmdResult(success: false, "Usage: dojoreplay <absolute_run_file_path> <global_floor>");
        }
        if (!int.TryParse(args[1], out int globalFloor))
        {
            return new CmdResult(success: false, "'" + args[1] + "' is not a valid floor number.");
        }

        RunHistory run;
        EncounterModel encounter;
        try
        {
            run = RunHistoryLoader.Load(args[0]);
            (_, MapPointRoomHistoryEntry combatRoom) = RunReconstructor.FindCombatFloor(run, globalFloor);
            ModelId encounterId = combatRoom.ModelId
                ?? throw new InvalidOperationException($"Floor {globalFloor}'s combat room is missing model_id.");
            encounter = ModelDb.GetById<EncounterModel>(encounterId).ToMutable();
        }
        catch (ModelNotFoundException e)
        {
            return new CmdResult(success: false,
                "Encounter not found (game content may have changed since this run): " + e.Message);
        }
        catch (Exception e)
        {
            return new CmdResult(success: false, "Failed to load/resolve floor " + globalFloor + ": " + e.Message);
        }
        encounter.DebugRandomizeRng();

        Task task = LaunchReplay(run, globalFloor, encounter);
        return new CmdResult(task, success: true,
            $"Dojo replay: floor {globalFloor} -> '{encounter.Id.Entry}'. Reconstructing loadout and launching...");
    }

    private static async Task LaunchReplay(RunHistory run, int globalFloor, EncounterModel encounter)
    {
        try
        {
            NGame? game = NGame.Instance;
            if (game == null)
            {
                MainFile.Logger.Error("[STS2Dojo] NGame.Instance is null; cannot launch Dojo replay.");
                return;
            }

            CharacterModel character = ModelDb.GetById<CharacterModel>(run.Players.Single().Character);
            RunState runState = await DojoLaunch.StartThrowawayRun(game, character, run.Ascension);
            Player player = runState.Players[0];

            // Snapshot the TRUE ascension-adjusted starting inventory (e.g. Ascender's Bane at high
            // ascension) that StartThrowawayRun just auto-populated, before we replace it.
            List<SerializableCard> startingDeck = player.Deck.Cards.Select(c => c.ToSerializable()).ToList();
            List<SerializableRelic> startingRelics = player.Relics.Select(r => r.ToSerializable()).ToList();
            int startingHp = player.Creature.MaxHp;
            int startingGold = player.Gold;

            ReconstructedLoadout loadout = RunReconstructor.Reconstruct(
                run, globalFloor, startingDeck, startingRelics, startingHp, startingGold);

            // Replace the auto-populated starting inventory with the reconstructed one. Cards use
            // silent:true + one InvokeCardAddFinished() flush at the end (CardPile's intended pattern for
            // a bulk rebuild — see NTopBarDeckButton, which only refreshes on CardAddFinished/
            // CardRemoveFinished, not the per-card CardAdded event). Relics/potions have no such batching
            // hook (NRelicInventory listens directly to RelicObtained/RelicRemoved), so those go non-silent.
            player.Deck.Clear(silent: true);
            foreach (RelicModel relic in player.Relics.ToList())
            {
                player.RemoveRelicInternal(relic);
            }
            foreach (PotionModel potion in player.Potions.ToList())
            {
                player.DiscardPotionInternal(potion);
            }

            foreach (ProvenancedCard pc in loadout.Deck)
            {
                // Must go through RunState.LoadCard (not CardModel.FromSerializable directly) — it's what
                // sets CardModel.Owner and registers the card with the run. Skipping it leaves Owner null,
                // which NREs the first time the hook system walks the deck (RunState.Contains).
                CardModel card = runState.LoadCard(pc.Card, player);
                player.Deck.AddInternal(card, index: -1, silent: true);
            }
            player.Deck.InvokeCardAddFinished();

            foreach (ProvenancedRelic pr in loadout.Relics)
            {
                player.AddRelicInternal(RelicModel.FromSerializable(pr.Relic));
            }
            // Potions ship empty in v1 (Assumed — see CLAUDE.md §10); already cleared above.

            player.Gold = loadout.Gold;
            player.Creature.SetMaxHpInternal(loadout.MaxHp);
            player.Creature.SetCurrentHpInternal(loadout.CurrentHp);

            MainFile.Logger.Info(
                $"[STS2Dojo] Replay launch: '{encounter.Id.Entry}' character={character.Id.Entry} " +
                $"deck={loadout.Deck.Count} relics={loadout.Relics.Count} hp={loadout.CurrentHp}/{loadout.MaxHp} " +
                $"gold={loadout.Gold} ascension={loadout.Ascension}.");

            await DojoLaunch.EnterEncounter(encounter);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Dojo replay launch failed: " + e);
        }
    }
}
