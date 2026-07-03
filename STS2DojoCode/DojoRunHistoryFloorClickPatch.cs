using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Wires "click a combat floor to replay it in the Dojo" onto the stock
/// <see cref="NMapPointHistoryEntry"/> floor icon, which has no click handling of its own. This Harmony
/// postfix is intentionally opt-in: only entries created through <see cref="CreateDojoEntry"/> are modified.
/// Stock Run History can still be opened through the Compendium in some modded sessions, so plain
/// <see cref="NMapPointHistoryEntry.Create"/> calls must remain visually and behaviorally unchanged.
///
/// Behavior matches the run-browser mock:
/// <list type="bullet">
/// <item>Non-combat floors (rest/shop/event/treasure) are left exactly as the stock strip draws them —
/// full color, hover tooltip intact — just unwired. They were never replay targets.</item>
/// <item>An ineligible combat floor (content no longer resolves — see <see cref="DojoFloorEligibility"/>)
/// is greyed out (<c>StsColors.disabledTopBarButton</c>) and left unwired, so it reads as unpickable
/// before a click. Its built-in hover-stats tooltip (<c>NMapPointHistoryEntry.OnFocus</c>) is deliberately
/// left working (no <c>Disable()</c> call) — the player may still want to see what happened on that
/// floor.</item>
/// <item>An eligible combat floor gets a <c>Released</c> handler that prompts and launches the replay.</item>
/// </list>
/// </summary>
[HarmonyPatch(typeof(NMapPointHistoryEntry), nameof(NMapPointHistoryEntry.Create))]
public static class DojoFloorClickPatch
{
    [ThreadStatic]
    private static int _dojoCreateDepth;

    /// <summary>Creates a stock run-history floor icon for the Dojo's in-row floor map. The scoped marker
    /// lets the <see cref="Postfix"/> distinguish Dojo-owned entries from the base game's own Run History
    /// screen, which also uses <see cref="NMapPointHistoryEntry.Create"/>.</summary>
    internal static NMapPointHistoryEntry CreateDojoEntry(
        RunHistory history, MapPointHistoryEntry entry, int floorNum)
    {
        _dojoCreateDepth++;
        try
        {
            return NMapPointHistoryEntry.Create(history, entry, floorNum);
        }
        finally
        {
            _dojoCreateDepth--;
        }
    }

    // ReSharper disable once UnusedMember.Global
    public static void Postfix(RunHistory history, MapPointHistoryEntry entry, int floorNum, NMapPointHistoryEntry __result)
    {
        if (_dojoCreateDepth <= 0)
        {
            return;
        }

        if (!DojoFloorEligibility.IsCombatFloor(entry))
        {
            // Not a fight — render as the stock strip would (untouched), no replay wiring.
            return;
        }

        if (!DojoFloorEligibility.IsEligible(history, entry, floorNum))
        {
            // Reuse the base game's own "disabled" tint (the same one it uses for a disabled top-bar
            // button) rather than inventing a new color. _Ready() (which runs later, once this node enters
            // the tree) only forces Modulate.A back to 1 — it leaves R/G/B untouched — so setting the grey
            // tint here survives. Nothing in the run-history entry path calls
            // NMapPointHistoryEntry.AnimateIn (confirmed by grepping decompiled/ for callers), so there's
            // no reveal-animation tween to fight either.
            __result.Modulate = StsColors.disabledTopBarButton;
            return;
        }

        __result.Released += _ => TaskHelper.RunSafely(DojoReplayConfirmation.ConfirmAndLaunch(
            history, floorNum, $"Replay this fight in the Dojo? (Floor {floorNum})"));
    }
}
