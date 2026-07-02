namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Where a Dojo fight's win/loss actually redirects to, once <c>DojoCombatEndPatches.cs</c> has decided to
/// skip the normal rewards/game-over flow for a Dojo run. Shows <see cref="DojoCompletionScreen"/> (Try
/// Again / Return to Dojo / Return to Main Menu — CLAUDE.md §9 roadmap item 2b) directly on top of the
/// still-alive combat scene via <c>NModalContainer</c>, the same way the game shows mid-run confirmations
/// like "abandon run?". No <c>NGame.ReturnToMainMenu()</c> call is needed here at all: unlike the old direct-
/// to-Run-History redirect, showing the completion screen involves no scene transition, so there's nothing
/// async to run inside the still-unwinding combat-end call stack (<c>NCombatUi.OnCombatWon</c> /
/// <c>CreatureCmd.Kill</c>). <c>ReturnToMainMenu()</c> (and the <c>RunManager.CleanUp()</c> inside it) only
/// runs later, once the player actually picks a button — by which point we're a fresh top-level Godot input
/// callback, not nested inside the original call stack.
/// </summary>
public static class DojoCombatEndInterceptor
{
    public static void HandleWin() => DojoCompletionScreen.Show(won: true);

    public static void HandleLoss() => DojoCompletionScreen.Show(won: false);
}
