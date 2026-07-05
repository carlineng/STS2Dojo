using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Reskins the top-bar room indicator (<see cref="NTopBarRoomIcon"/>) for Dojo fights so it reads as a
/// "Dojo" node instead of the stock "Antechamber" placeholder.
///
/// A Dojo fight is launched with no <c>CurrentMapPoint</c> (see CLAUDE.md §8.0 — <c>EnterRoomDebug</c> with
/// <c>MapPointType.Unassigned</c>), so <c>GetCurrentMapPointType()</c> falls through to
/// <c>MapPointType.Unassigned</c>. Stock behavior for that state:
/// <list type="bullet">
/// <item>the hover tip is built from the <c>"ROOM_MAP"</c> prefix, whose <c>static_hover_tips</c> loc entries
/// read "Antechamber" / "A place where you can plan your next move";</item>
/// <item><c>ImageHelper.GetRoomIconPath</c> returns <c>null</c> for <c>Unassigned</c>, so the icon
/// (<c>_roomIcon</c>) is hidden — the blank area the player sees.</item>
/// </list>
///
/// Both patches are gated on <see cref="DojoRunRegistry.IsCurrentRunDojo"/> so real player runs (which always
/// have a real <c>CurrentMapPoint</c> and never hit this state anyway) are completely untouched.
///
/// The mod has no loc table of its own, but the game exposes <c>LocTable.MergeWith</c>, so we register real
/// <c>"ROOM_DOJO.*"</c> entries into the existing <c>static_hover_tips</c> table and just swap the prefix.
/// This keeps the hover tip flowing through the game's own <c>HoverTip</c>/<c>NHoverTipSet</c> pipeline
/// (<c>NHoverTipSet.Init</c> hard-matches the concrete <c>HoverTip</c> struct, so a hand-rolled
/// <c>IHoverTip</c> would render nothing) with no reflection.
/// </summary>
public static class DojoTopBarRoomIconPatch
{
    private const string LocTableName = "static_hover_tips";
    private const string StockMapPrefix = "ROOM_MAP";
    private const string DojoPrefix = "ROOM_DOJO";
    private const string TitleKey = DojoPrefix + ".title";
    private const string DescriptionKey = DojoPrefix + ".description";

    /// <summary>Registers the "Dojo" title/description into the stock <c>static_hover_tips</c> table if not
    /// already present. Idempotent, and re-runs cheaply after a locale change (which rebuilds the tables and
    /// drops the merged entries) because the existence check will fail against the fresh table.</summary>
    private static void EnsureDojoLoc()
    {
        if (LocString.Exists(LocTableName, TitleKey))
        {
            return;
        }

        LocManager.Instance.GetTable(LocTableName).MergeWith(new Dictionary<string, string>
        {
            [TitleKey] = "Dojo",
            [DescriptionKey] = "A place to train for your next adventure.",
        });
    }

    /// <summary>Swaps the hover-tip prefix from the stock "Antechamber" (<c>ROOM_MAP</c>) to <c>ROOM_DOJO</c>
    /// for Dojo runs. Matching on the result being exactly <c>ROOM_MAP</c> means only the Unassigned/blank
    /// state is affected — a Dojo fight with a real room type (there are none today, but defensively) is left
    /// alone.</summary>
    [HarmonyPatch(typeof(NTopBarRoomIcon), "GetHoverTipPrefixForRoomType")]
    public static class HoverTipPrefixPatch
    {
        // ReSharper disable once UnusedMember.Global
        public static void Postfix(ref string __result)
        {
            if (__result != StockMapPrefix || !DojoRunRegistry.IsCurrentRunDojo())
            {
                return;
            }

            EnsureDojoLoc();
            __result = DojoPrefix;
        }
    }

    /// <summary>Draws the standard hallway monster-fight icon in place of the blank Antechamber icon for Dojo
    /// runs. Runs as a postfix so stock <c>UpdateIcon</c> has already resolved (and hidden) the icon; we only
    /// override the exact blank case (<c>CurrentMapPoint == null</c>, not a victory room). The path is a real
    /// texture (<c>ui/run_history/monster.png</c>), so no missing-texture red-box placeholder appears.</summary>
    [HarmonyPatch(typeof(NTopBarRoomIcon), "UpdateIcon")]
    public static class RoomIconTexturePatch
    {
        // ReSharper disable once UnusedMember.Global
        public static void Postfix(NTopBarRoomIcon __instance)
        {
            if (!DojoRunRegistry.IsCurrentRunDojo())
            {
                return;
            }

            var runState = Traverse.Create(__instance).Field("_runState").GetValue<IRunState>();
            // Only reskin the true Antechamber state; leave anything the stock code gave a real icon untouched.
            if (runState?.CurrentRoom == null || runState.CurrentMapPoint != null || runState.BaseRoom?.IsVictoryRoom == true)
            {
                return;
            }

            string? iconPath = ImageHelper.GetRoomIconPath(MapPointType.Monster, RoomType.Monster, null);
            string? outlinePath = ImageHelper.GetRoomIconOutlinePath(MapPointType.Monster, RoomType.Monster, null);
            if (iconPath == null)
            {
                return;
            }

            ApplyTexture(Traverse.Create(__instance).Field("_roomIcon").GetValue<TextureRect>(), iconPath);
            ApplyTexture(Traverse.Create(__instance).Field("_roomIconOutline").GetValue<TextureRect>(), outlinePath);
        }

        private static void ApplyTexture(TextureRect? target, string? path)
        {
            if (target == null || path == null)
            {
                return;
            }

            target.Visible = true;
            target.Texture = PreloadManager.Cache.GetCompressedTexture2D(path);
        }
    }
}
