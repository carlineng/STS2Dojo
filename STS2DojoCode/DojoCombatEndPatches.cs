using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Combat-end interception for Dojo runs (CLAUDE.md §9 roadmap item 2), plus suppression of the save
/// leaks that persist even with <c>shouldSave:false</c> (roadmap item 3). All patches are gated by
/// <see cref="DojoRunRegistry.IsCurrentRunDojo"/> so real player runs are completely unaffected.
///
/// There is no single "combat ended" hook that covers both win and loss: an ordinary win never calls
/// <c>RunManager.OnEnded</c> at all (only a full-run "final boss" victory does) — the real win hook is
/// <c>CombatManager.CombatWon</c>, whose only other subscriber besides <c>NCombatUi.OnCombatWon</c> is the
/// cosmetic <c>NCombatRoom.RestrictControllerNavigation</c>, left untouched. Because
/// <c>CombatManager.CombatWon</c> is a plain multicast event, an additional subscriber can't preempt
/// <c>NCombatUi.OnCombatWon</c>'s reward flow — patching it directly is required. Loss detection is a
/// direct synchronous call inside <c>CreatureCmd.Kill</c> (not an event); the earliest reliable, public
/// patch point downstream of it is <c>NRun.ShowGameOverScreen</c>, called immediately afterward.
/// </summary>
[HarmonyPatch(typeof(NCombatUi), "OnCombatWon")]
public static class DojoSkipRewardsPatch
{
    // ReSharper disable once UnusedMember.Global
    public static bool Prefix(CombatRoom room)
    {
        if (!DojoRunRegistry.IsCurrentRunDojo())
        {
            return true;
        }

        MainFile.Logger.Info("[STS2Dojo] Dojo combat won — skipping rewards/map, returning to Run History.");
        DojoCombatEndInterceptor.HandleWin();
        return false;
    }
}

[HarmonyPatch(typeof(NRun), nameof(NRun.ShowGameOverScreen))]
public static class DojoSkipGameOverPatch
{
    // ReSharper disable once UnusedMember.Global
    public static bool Prefix()
    {
        if (!DojoRunRegistry.IsCurrentRunDojo())
        {
            return true;
        }

        MainFile.Logger.Info("[STS2Dojo] Dojo combat lost — skipping game-over screen, returning to Run History.");
        DojoCombatEndInterceptor.HandleLoss();
        return false;
    }
}

/// <summary>Blocks the win-path progress.save write. CombatManager.EndCombatInternal calls this
/// unconditionally (NOT gated by RunManager.ShouldSave) right after UpdateProgressAfterCombatWon.</summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveProgressFile))]
public static class DojoSuppressProgressSavePatch
{
    // ReSharper disable once UnusedMember.Global
    public static bool Prefix() => !DojoRunRegistry.IsCurrentRunDojo();
}

/// <summary>Blocks the win-path in-memory Progress mutation (win tallies, boss/elite epoch unlocks, enemy
/// discovery) at its source — belt-and-suspenders beyond just blocking the file write, since a later
/// unrelated real save could otherwise flush Dojo-tainted Progress state to disk.</summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.UpdateProgressAfterCombatWon))]
public static class DojoSuppressCombatWinProgressPatch
{
    // ReSharper disable once UnusedMember.Global
    public static bool Prefix(Player localPlayer, CombatRoom combatRoom) => !DojoRunRegistry.IsCurrentRunDojo();
}

/// <summary>Blocks the loss-path enemy-discovery mutation inside RunManager.OnEnded — the one unconditional
/// (non-ShouldSave-gated) side effect CLAUDE.md's original spike identified.</summary>
[HarmonyPatch(typeof(RunManager), "CheckUpdateEnemyDiscoveryAfterLoss")]
public static class DojoSuppressEnemyDiscoveryPatch
{
    // ReSharper disable once UnusedMember.Global
    public static bool Prefix() => !DojoRunRegistry.IsCurrentRunDojo();
}
