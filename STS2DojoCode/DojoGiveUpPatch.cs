using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// "Give Up" (the pause-menu give-up button AND the Settings screen's Abandon Run button) both funnel through
/// <see cref="RunManager.Abandon"/> → <c>AbandonInternal</c>, which force-kills every player and then relies on
/// the normal death → <c>NRun.ShowGameOverScreen</c> chain to end the run. For a real run that reaches the
/// game-over screen; for a Dojo run it's supposed to reach the mod's <c>DojoSkipGameOverPatch</c> and show
/// <see cref="DojoCompletionScreen"/> — but empirically it does not: the player creature dies yet the fight
/// stays on screen with no completion/game-over UI (the abandon path does extra teardown — closing
/// <see cref="NCapstoneContainer"/>/<c>NMapScreen</c>, both of which are absent/degenerate in a Dojo fight
/// launched via <c>EnterRoomDebug</c> with no real map — and that side of the flow can leave the win-condition
/// redirect from stranding the fight active).
///
/// Rather than depend on that fragile kill → game-over chain in the Dojo's map-less context, this intercepts
/// <c>Abandon</c> for Dojo runs only and does exactly what the user wants a Give Up to do: end the fight and
/// return straight to the Dojo screen — the same destination as <see cref="DojoCompletionScreen"/>'s "Return
/// to Dojo" button (<c>NGame.ReturnToMainMenu()</c>, whose inner <c>RunManager.CleanUp()</c> tears the run down
/// and unmarks it via <c>DojoUnmarkOnCleanUpPatch</c>, then <c>NDojoScreen.Open</c>). Real runs are untouched
/// (the gate returns true → original runs).
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.Abandon))]
public static class DojoGiveUpPatch
{
    // ReSharper disable once UnusedMember.Global
    public static bool Prefix()
    {
        if (!DojoRunRegistry.IsCurrentRunDojo())
        {
            return true;
        }

        MainFile.Logger.Info("[STS2Dojo] Dojo run 'Give Up' — ending fight, returning to the Dojo screen.");
        TaskHelper.RunSafely(ReturnToDojo());
        return false;
    }

    private static async Task ReturnToDojo()
    {
        // The give-up button lives in the pause menu / Settings submenu inside the capstone container, which
        // (unlike the completion screen's case) is open when this fires. ReturnToMainMenu swaps the scene, but
        // close it explicitly first — the same thing the game's own AbandonInternal does — so it doesn't linger
        // through the fade. The abandon-confirm popup lives in the modal container; clear that too.
        NCapstoneContainer.Instance?.Close();
        NModalContainer.Instance?.Clear();

        NGame? game = NGame.Instance;
        if (game == null)
        {
            return;
        }

        await game.ReturnToMainMenu();
        NDojoScreen.Open(game);
    }
}
