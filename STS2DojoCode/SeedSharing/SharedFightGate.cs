using System;

namespace STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>
/// The §12c compatibility gate (revised 2026-07-04): refuse on game build_id mismatch or payload
/// schema_version mismatch, BEFORE the content-eligibility check runs. Content can resolve fine (same
/// card/relic ids) while shuffle/RNG-consumption logic changed between builds, silently breaking the
/// identical-experience promise with no missing-content error to catch it. The mod version recorded in
/// the payload is diagnostics-only and deliberately NOT gated — fight determinism is driven by game
/// code, and payload format changes are already covered by the schema version.
/// </summary>
public static class SharedFightGate
{
    /// <summary>Null when the payload may be imported; otherwise a complete, user-presentable refusal
    /// message (§12e: show it verbatim, no stack traces).</summary>
    public static string? GetRefusal(SharedFightPayload payload, string currentGameBuildId)
    {
        if (payload.SchemaVersion != SharedFightPayload.CurrentSchemaVersion)
        {
            return payload.SchemaVersion > SharedFightPayload.CurrentSchemaVersion
                ? "This fight was exported by a newer version of the Dojo mod " +
                  $"(format v{payload.SchemaVersion}, this mod reads v{SharedFightPayload.CurrentSchemaVersion}). " +
                  "Update the mod to import it."
                : "This fight was exported by an older version of the Dojo mod " +
                  $"(format v{payload.SchemaVersion}, this mod reads v{SharedFightPayload.CurrentSchemaVersion}) " +
                  "and can no longer be imported.";
        }

        string payloadBuild = payload.GameBuildId.Trim();
        string currentBuild = currentGameBuildId.Trim();
        if (!string.Equals(payloadBuild, currentBuild, StringComparison.Ordinal))
        {
            return $"This fight was captured on game version {payloadBuild}, but this game is " +
                   $"{currentBuild}. Fights only replay identically on the exact same game version, " +
                   "so it can't be imported.";
        }

        return null;
    }
}
