using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using STS2Dojo.STS2DojoCode.Reconstruction;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Wires "click a combat floor in the Dojo run browser to replay it" onto <see cref="NMapPointHistoryEntry"/>,
/// which has no click handling of its own today (CLAUDE.md §9 roadmap item 5). This postfix fires for every
/// <see cref="NMapPointHistoryEntry"/> created, but that's harmless: Run History (and therefore this screen)
/// is otherwise unreachable when modded — Compendium is hidden — so in practice it only ever fires for a
/// Dojo-opened <see cref="NRunHistory"/> (<see cref="DojoRunBrowser"/>).
/// </summary>
[HarmonyPatch(typeof(NMapPointHistoryEntry), nameof(NMapPointHistoryEntry.Create))]
public static class DojoFloorClickPatch
{
    // ReSharper disable once UnusedMember.Global
    public static void Postfix(RunHistory history, MapPointHistoryEntry entry, int floorNum, NMapPointHistoryEntry __result)
    {
        // Matches the Dojo's own eligibility rules (CLAUDE.md §6/§10): single-player only, no
        // modifier-bearing runs, and only floors with an actual combat room.
        if (!RunHistoryQueries.IsSinglePlayer(history))
        {
            return;
        }
        if (history.Modifiers.Count > 0)
        {
            return;
        }
        if (!entry.Rooms.Any(RunHistoryQueries.IsCombatRoom))
        {
            return;
        }

        __result.Released += _ => TaskHelper.RunSafely(ConfirmAndLaunch(history, floorNum));
    }

    private static async Task ConfirmAndLaunch(RunHistory history, int floorNum)
    {
        NGenericPopup? popup = NGenericPopup.Create();
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (popup == null || modalContainer == null)
        {
            return;
        }

        modalContainer.Add(popup);

        // NGenericPopup.WaitForConfirmation only accepts LocString header/body, which resolve against the
        // base game's localization tables — this mod has none. Bypassing it for the raw-string SetText
        // overload on the underlying NVerticalPopup (same node NGenericPopup itself uses internally) avoids
        // needing new loc-table entries just for this dialog's text. The Yes/No button labels DO go through
        // LocString, but reuse two keys that already exist in the base game's own tables.
        NVerticalPopup verticalPopup = popup.GetNode<NVerticalPopup>("VerticalPopup");
        verticalPopup.SetText("Dojo", $"Replay this fight in the Dojo? (Floor {floorNum})");

        var confirmation = new TaskCompletionSource<bool>();
        verticalPopup.InitYesButton(
            new LocString("main_menu_ui", "GENERIC_POPUP.confirm"), _ => confirmation.TrySetResult(true));
        verticalPopup.InitNoButton(
            new LocString("main_menu_ui", "GENERIC_POPUP.cancel"), _ => confirmation.TrySetResult(false));

        if (await confirmation.Task)
        {
            await DojoReplayLauncher.LaunchReplay(history, floorNum);
        }
    }
}
