using System;
using Godot;
using MegaCrit.Sts2.Core.Debug;

namespace STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>
/// Game-side packaging step shared by every §12a export entry point: snapshot → payload (stamping
/// build/mod/metadata) → Saved Fights library file + compact code on the system clipboard. Kept out of
/// the codec/library so those stay pure and test-compilable — this is the only seed-sharing class that
/// touches Godot (clipboard, user:// path) or game singletons (ReleaseInfoManager).
/// </summary>
public static class SharedFightExporter
{
    /// <summary>Recorded in payloads for diagnostics only (§12c — never gated). Keep in sync with
    /// STS2Dojo.json's "version".</summary>
    public const string ModVersion = "v0.0.0";

    /// <summary>Inside the mod's existing <c>user://sts2dojo/</c> home — global to the install and
    /// structurally disjoint from every profile-scoped path SaveManager touches (§12f).</summary>
    public static string FightsDirectory => ProjectSettings.GlobalizePath("user://sts2dojo/fights");

    /// <summary>Same source the game stamps into `.run` files as build_id (RunHistoryUtilities).</summary>
    public static string CurrentGameBuildId =>
        ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "NON-RELEASE-VERSION";

    public sealed record ExportResult(bool Success, string Message, string? Code);

    /// <summary>Packages and exports a captured snapshot. Never throws: the result's Message is always
    /// presentable button/label text. v1 titles are auto-generated ("Encounter — date"); editing
    /// metadata is an explicit fast-follow (§12h).</summary>
    public static ExportResult Export(DojoFightSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            return new ExportResult(false, "Nothing to export", null);
        }

        try
        {
            DateTime now = DateTime.UtcNow;
            SharedFightPayload payload = SharedFightPayload.FromSnapshot(
                snapshot,
                gameBuildId: CurrentGameBuildId,
                modVersion: ModVersion,
                title: DojoDisplayNames.Encounter(snapshot.EncounterId) + " — " +
                       now.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                comment: string.Empty,
                createdUtc: now);

            DojoFightLibrary.Save(FightsDirectory, payload, SavedFightOrigin.Created,
                message => MainFile.Logger.Info(message));

            string code = SharedFightCodec.ToCode(payload);
            DisplayServer.ClipboardSet(code);
            MainFile.Logger.Info(
                $"[STS2Dojo] Exported fight '{payload.Title}' ({code.Length}-char code copied to clipboard).");
            return new ExportResult(true, "Exported — code copied!", code);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Fight export failed: " + e);
            return new ExportResult(false, "Export failed (see log)", null);
        }
    }
}
