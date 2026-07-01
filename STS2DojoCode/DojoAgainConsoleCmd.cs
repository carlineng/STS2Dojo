using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// STS2 Dojo "try again": relaunches the last <c>dojo</c>/<c>dojoreplay</c> fight with the same loadout
/// (fresh combat RNG — CLAUDE.md §3). Console-only for now; no confirm-dialog/button UI exists yet
/// (CLAUDE.md §9 roadmap item 5 is separate future work).
///
/// Usage in the dev console:  <c>dojoagain</c>
/// </summary>
public class DojoAgainConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "dojoagain";

    public override string Args => "";

    public override string Description =>
        "STS2 Dojo: relaunch the last dojo/dojoreplay fight with the same loadout (fresh combat RNG).";

    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        Task task = RetryLastLaunch();
        return new CmdResult(task, success: true, "Dojo: retrying last launch...");
    }

    private static async Task RetryLastLaunch()
    {
        try
        {
            if (await DojoLaunch.TryAgain() == null)
            {
                MainFile.Logger.Error("[STS2Dojo] dojoagain: no previous dojo/dojoreplay launch to retry.");
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] dojoagain failed: " + e);
        }
    }
}
