using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Wires "click a combat floor in the Dojo run browser to replay it" onto <see cref="NMapPointHistoryEntry"/>,
/// which has no click handling of its own today (CLAUDE.md §9 roadmap item 5). This postfix fires for every
/// <see cref="NMapPointHistoryEntry"/> created, but that's harmless: Run History (and therefore this screen)
/// is otherwise unreachable when modded — Compendium is hidden — so in practice it only ever fires for a
/// Dojo-opened <see cref="NRunHistory"/> (<see cref="DojoRunBrowser"/>).
///
/// Ineligible floors (per <see cref="DojoFloorEligibility"/>) are greyed out and left unwired rather than
/// silently doing nothing on click — the existing built-in hover-stats tooltip (<c>NMapPointHistoryEntry
/// .OnFocus</c>) is deliberately left alone (no <c>Disable()</c> call), since it's core game functionality
/// unrelated to replay eligibility and the player may still want to see what happened on that floor.
///
/// Eligible floors are additionally shown pre-highlighted (large icon, bright outline — the same visual
/// state <c>NMapPointHistoryEntry.Highlight()</c> normally only reaches on hover), so they read as "pick
/// one of these" at a glance instead of the player having to mouse over every floor to discover which ones
/// are replayable. See <see cref="DojoFloorHighlightPatches"/>.
/// </summary>
[HarmonyPatch(typeof(NMapPointHistoryEntry), nameof(NMapPointHistoryEntry.Create))]
public static class DojoFloorClickPatch
{
    private static readonly ConditionalWeakTable<NMapPointHistoryEntry, object> EligibleEntries = new();
    private static readonly object EligibleMarker = new();

    /// <summary>Whether <paramref name="entry"/> was marked eligible by this patch's own <see cref="Postfix"/>.
    /// Backs <see cref="DojoFloorHighlightPatches"/> — a <see cref="ConditionalWeakTable{TKey,TValue}"/>
    /// rather than a plain set/dictionary so it never keeps a Godot node alive past the run browser closing.</summary>
    internal static bool IsMarkedEligible(NMapPointHistoryEntry entry) => EligibleEntries.TryGetValue(entry, out _);

    // ReSharper disable once UnusedMember.Global
    public static void Postfix(RunHistory history, MapPointHistoryEntry entry, int floorNum, NMapPointHistoryEntry __result)
    {
        if (!DojoFloorEligibility.IsEligible(history, entry, floorNum))
        {
            // Reuse the base game's own "disabled" tint (same one it uses for a disabled top-bar button)
            // rather than inventing a new color. _Ready() (which runs later, once this node enters the
            // tree) only forces Modulate.A back to 1 — it leaves R/G/B untouched — so setting the grey
            // tint here survives. Nothing in the Run History screen calls
            // NMapPointHistoryEntry.AnimateIn (confirmed by grepping decompiled/ for callers - it appears
            // unused for this screen), so there's no reveal-animation tween to fight either.
            __result.Modulate = StsColors.disabledTopBarButton;
            return;
        }

        EligibleEntries.Add(__result, EligibleMarker);
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

/// <summary>
/// Makes eligible floors start in <see cref="NMapPointHistoryEntry.Highlight"/>'s "large icon, bright
/// outline" state and stay there, so hovering an eligible floor only has to show the tooltip — the icon is
/// already at its highlighted size. Two small patches:
/// <list type="bullet">
/// <item><c>_Ready</c> postfix: calls <c>Highlight()</c> once for eligible floors, right after the node's
/// texture/outline references exist (they're null until <c>_Ready()</c> runs, so this can't happen any
/// earlier — e.g. not from <see cref="DojoFloorClickPatch"/>'s own <c>Create</c> postfix).</item>
/// <item><c>Unhighlight</c> prefix: skipped entirely for eligible floors, so focus loss (mouse-out,
/// controller nav away) never shrinks them back down.</item>
/// </list>
/// <c>OnFocus</c>/<c>OnUnfocus</c> (and the tooltip logic inside them) are untouched — <c>Highlight()</c>
/// still runs on hover for an eligible floor, it's just a no-op tween (already at the target scale/color),
/// and the tooltip show/hide is unaffected either way.
/// </summary>
[HarmonyPatch(typeof(NMapPointHistoryEntry))]
public static class DojoFloorHighlightPatches
{
    [HarmonyPatch(nameof(NMapPointHistoryEntry._Ready))]
    [HarmonyPostfix]
    // ReSharper disable once UnusedMember.Global
    public static void ReadyPostfix(NMapPointHistoryEntry __instance)
    {
        if (DojoFloorClickPatch.IsMarkedEligible(__instance))
        {
            __instance.Highlight();
        }
    }

    [HarmonyPatch(nameof(NMapPointHistoryEntry.Unhighlight))]
    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Global
    public static bool UnhighlightPrefix(NMapPointHistoryEntry __instance) =>
        !DojoFloorClickPatch.IsMarkedEligible(__instance);
}
