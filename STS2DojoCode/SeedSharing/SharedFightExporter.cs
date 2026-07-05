using System;
using Godot;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Platform;

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

    /// <summary>The local player's platform (Steam) display name, stamped as the fight's author. Empty
    /// (never throws) if the platform layer isn't available — e.g. a non-Steam/null-platform launch.</summary>
    public static string CurrentAuthorName
    {
        get
        {
            try
            {
                PlatformType platform = PlatformUtil.PrimaryPlatform;
                ulong localId = PlatformUtil.GetLocalPlayerId(platform);
                string name = PlatformUtil.GetPlayerNameRaw(platform, localId);
                return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
            }
            catch (Exception e)
            {
                MainFile.Logger.Info("[STS2Dojo] Could not resolve local player name for fight author: " + e.Message);
                return string.Empty;
            }
        }
    }

    public sealed record ExportResult(bool Success, string Message, string? Code);

    /// <summary>Packages a captured snapshot and copies its share code to the clipboard WITHOUT writing a
    /// library entry (the export-flow split asked for 2026-07: copy vs. save are now separate player
    /// choices). Never throws: the result's Message is always presentable button/label text.</summary>
    public static ExportResult CopyCode(DojoFightSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            return new ExportResult(false, "Nothing to export", null);
        }

        try
        {
            SharedFightPayload payload = BuildPayload(snapshot);
            string code = SharedFightCodec.ToCode(payload);
            DisplayServer.ClipboardSet(code);
            MainFile.Logger.Info(
                $"[STS2Dojo] Copied fight code for '{payload.Title}' ({code.Length} chars) to clipboard.");
            return new ExportResult(true, "Code copied to clipboard!", code);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Fight code copy failed: " + e);
            return new ExportResult(false, "Copy failed (see log)", null);
        }
    }

    /// <summary>Packages a captured snapshot and saves it to the Saved Fights library WITHOUT touching the
    /// clipboard. The counterpart to <see cref="CopyCode"/>. Never throws.</summary>
    public static ExportResult SaveToLibrary(DojoFightSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            return new ExportResult(false, "Nothing to save", null);
        }

        try
        {
            SharedFightPayload payload = BuildPayload(snapshot);
            DojoFightLibrary.Save(FightsDirectory, payload, SavedFightOrigin.Created,
                message => MainFile.Logger.Info(message));
            return new ExportResult(true, "Saved to Saved Fights!", null);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error("[STS2Dojo] Fight save failed: " + e);
            return new ExportResult(false, "Save failed (see log)", null);
        }
    }

    /// <summary>Snapshot → payload, stamping build/mod/auto-metadata. v1 titles are auto-generated
    /// ("Encounter — date"); the title/comment are editable afterwards from the Saved Fights screen (§12h).</summary>
    private static SharedFightPayload BuildPayload(DojoFightSnapshot snapshot)
    {
        DateTime now = DateTime.UtcNow;
        return SharedFightPayload.FromSnapshot(
            snapshot,
            gameBuildId: CurrentGameBuildId,
            modVersion: ModVersion,
            title: DojoDisplayNames.Encounter(snapshot.EncounterId) + " — " +
                   now.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            comment: string.Empty,
            author: CurrentAuthorName,
            createdUtc: now);
    }
}
