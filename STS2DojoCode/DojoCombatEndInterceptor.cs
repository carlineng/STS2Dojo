using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Where a Dojo fight's win/loss actually redirects to, once <c>DojoCombatEndPatches.cs</c> has decided to
/// skip the normal rewards/game-over flow for a Dojo run. Reuses the game's own "return to main menu" path
/// (<c>NGame.ReturnToMainMenu</c> — the same one the pause menu's "quit run" and the game-over screen's
/// "Main Menu" button call) and then lands on Run History specifically, which nothing built-in does in one
/// call. <c>PushSubmenuType&lt;NRunHistory&gt;()</c> is the same call the Compendium menu uses to reach
/// Run History normally.
///
/// Both handlers are fire-and-forget via <see cref="TaskHelper.RunSafely"/>, matching the game's own
/// pattern (e.g. <c>NCombatUi.OnCombatWon</c>'s reward flow). This matters here specifically because
/// <c>ReturnToMainMenu()</c>'s first statement is an awaited fade-out — a real async yield — so
/// <c>RunManager.CleanUp()</c> (called later inside it) never runs synchronously inside the still-unwinding
/// combat-end call stack (<c>NCombatUi.OnCombatWon</c> / <c>CreatureCmd.Kill</c>), which would otherwise
/// risk <c>CombatManager.Reset()</c> mutating state the original call stack reads afterward.
///
/// Note: the modded profile's Run History is empty today — feeding it the real profile's <c>.run</c> files
/// is CLAUDE.md §9 roadmap item 5, separate future work. Landing on an empty screen here is expected.
/// </summary>
public static class DojoCombatEndInterceptor
{
    public static void HandleWin() => TaskHelper.RunSafely(ReturnToRunHistoryAsync());

    public static void HandleLoss() => TaskHelper.RunSafely(ReturnToRunHistoryAsync());

    private static async Task ReturnToRunHistoryAsync()
    {
        NGame? game = NGame.Instance;
        if (game == null)
        {
            return;
        }

        await game.ReturnToMainMenu();
        game.MainMenu?.SubmenuStack.PushSubmenuType<NRunHistory>();
    }
}
