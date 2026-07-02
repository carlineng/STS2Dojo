using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Exceptions;
using MegaCrit.Sts2.Core.Runs;
using STS2Dojo.STS2DojoCode.Reconstruction;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Reconstructor integration test (see CLAUDE.md §9 roadmap item 1). Loads a real <c>.run</c> history
/// file from an absolute path, reconstructs the loadout entering the fight on a given global floor
/// (RunReconstructor.cs), and launches it through the same §8.0 throwaway-run sequence as <c>dojo</c> —
/// replacing the hardcoded junk loadout with real reconstructed deck/relics/hp/gold. The actual
/// reconstruction+launch pipeline lives in <see cref="DojoReplayLauncher"/>, shared with the Dojo run
/// browser's floor-click handler (CLAUDE.md §9 roadmap item 5).
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
        ModelId encounterId;
        try
        {
            run = RunHistoryLoader.Load(args[0]);
            encounterId = DojoReplayLauncher.ResolveEncounterId(run, globalFloor);
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

        Task task = DojoReplayLauncher.LaunchReplay(run, globalFloor, encounterId);
        return new CmdResult(task, success: true,
            $"Dojo replay: floor {globalFloor} -> '{encounterId.Entry}'. Reconstructing loadout and launching...");
    }
}
