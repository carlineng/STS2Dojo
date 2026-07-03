using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// The "Replay this fight in the Dojo?" confirm dialog + launch, shared by the stock run-history
/// floor-click patch (<c>DojoRunHistoryFloorClickPatch.cs</c>) and the custom Dojo screen's fight pills
/// (<see cref="NDojoScreen"/>). Only the message text differs per call site.
/// </summary>
public static class DojoReplayConfirmation
{
    public static async Task ConfirmAndLaunch(RunHistory history, int floor, string message)
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
        // overload on the underlying NVerticalPopup (same node NGenericPopup itself uses internally)
        // avoids needing new loc-table entries just for this dialog's text. The Yes/No button labels DO
        // go through LocString, but reuse two keys that already exist in the base game's own tables.
        NVerticalPopup verticalPopup = popup.GetNode<NVerticalPopup>("VerticalPopup");
        verticalPopup.SetText("Dojo", message);

        var confirmation = new TaskCompletionSource<bool>();
        verticalPopup.InitYesButton(
            new LocString("main_menu_ui", "GENERIC_POPUP.confirm"), _ => confirmation.TrySetResult(true));
        verticalPopup.InitNoButton(
            new LocString("main_menu_ui", "GENERIC_POPUP.cancel"), _ => confirmation.TrySetResult(false));

        if (await confirmation.Task)
        {
            await DojoReplayLauncher.LaunchReplay(history, floor);
        }
    }
}
