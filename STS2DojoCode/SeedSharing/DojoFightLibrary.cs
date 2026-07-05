using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>Importer-local, deliberately NOT part of the shared payload: the same payload is "created
/// by you" on the exporter's machine and "imported" on everyone else's (§12g's badge).</summary>
public enum SavedFightOrigin
{
    Created,
    Imported
}

public sealed record SavedFightEntry(
    string FilePath,
    SharedFightPayload Payload,
    SavedFightOrigin Origin);

public sealed record SavedFightListing(
    IReadOnlyList<SavedFightEntry> Entries,
    int UnreadableFiles);

/// <summary>
/// The §12f/§12g "Saved Fights" store: one payload-JSON file per saved fight in a mod-owned directory.
/// Callers supply the directory (in-game: <c>ProjectSettings.GlobalizePath("user://sts2dojo/fights")</c>
/// — inside the mod's existing <c>user://sts2dojo/</c> home, structurally disjoint from every
/// profile-scoped path <c>SaveManager</c>/<c>UserDataPathProvider</c> read or write, per §12f; global to
/// the install, not per-profile). Origin (created-by-you vs imported) is encoded in the filename, not
/// the payload — see <see cref="SavedFightOrigin"/>. v1 creates and reads entries only; edit/delete is
/// an explicit fast-follow (§12h).
/// </summary>
public static class DojoFightLibrary
{
    public const string FileExtension = ".dojofight.json";

    /// <summary>Saves a payload as a new library entry and returns its full path. File writes go
    /// through a temp-file + move so a crash mid-write can't leave a half-written entry that
    /// <see cref="List"/> would count as unreadable forever.</summary>
    public static string Save(
        string directory, SharedFightPayload payload, SavedFightOrigin origin, Action<string>? log = null)
    {
        Directory.CreateDirectory(directory);

        string fileName = BuildFileName(
            origin, payload.CreatedUtc, payload.Title,
            candidate => File.Exists(Path.Combine(directory, candidate)));
        string finalPath = Path.Combine(directory, fileName);

        string tempPath = finalPath + ".tmp";
        File.WriteAllText(tempPath, SharedFightCodec.ToJson(payload));
        File.Move(tempPath, finalPath);

        log?.Invoke($"[STS2Dojo] Saved fight '{payload.Title}' to {finalPath}.");
        return finalPath;
    }

    /// <summary>Loads every readable entry, newest-created first. Unreadable/damaged files are counted
    /// (for an aggregate "N unreadable" UI note) and logged, never thrown.</summary>
    public static SavedFightListing List(string directory, Action<string>? log = null)
    {
        if (!Directory.Exists(directory))
        {
            return new SavedFightListing([], 0);
        }

        List<SavedFightEntry> entries = [];
        int unreadable = 0;
        foreach (string path in Directory.GetFiles(directory))
        {
            string fileName = Path.GetFileName(path);
            SavedFightOrigin? origin = TryClassify(fileName);
            if (origin == null)
            {
                continue; // not a library entry (e.g. a stray .tmp or unrelated file) — not an error
            }

            try
            {
                SharedFightPayload payload = SharedFightCodec.FromJson(File.ReadAllText(path));
                entries.Add(new SavedFightEntry(path, payload, origin.Value));
            }
            catch (Exception e) when (e is IOException or SharedFightFormatException or UnauthorizedAccessException)
            {
                unreadable++;
                log?.Invoke($"[STS2Dojo] Skipping unreadable saved fight '{fileName}': {e.Message}");
            }
        }

        entries.Sort((a, b) => b.Payload.CreatedUtc.CompareTo(a.Payload.CreatedUtc));
        return new SavedFightListing(entries, unreadable);
    }

    /// <summary><c>{origin}-{utc timestamp}-{title slug}{FileExtension}</c>, with a numeric suffix on
    /// collision. <paramref name="fileNameExists"/> is a seam so the pure logic is unit-testable.</summary>
    public static string BuildFileName(
        SavedFightOrigin origin, DateTime createdUtc, string title, Func<string, bool> fileNameExists)
    {
        string stem = OriginPrefix(origin) + "-" + createdUtc.ToString("yyyyMMdd-HHmmss") + "-" + Slugify(title);

        string candidate = stem + FileExtension;
        for (int n = 2; fileNameExists(candidate); n++)
        {
            candidate = stem + "-" + n + FileExtension;
        }
        return candidate;
    }

    /// <summary>Null when the file is not a library entry; otherwise its origin, parsed back off the
    /// filename prefix.</summary>
    public static SavedFightOrigin? TryClassify(string fileName)
    {
        if (!fileName.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (fileName.StartsWith(OriginPrefix(SavedFightOrigin.Created) + "-", StringComparison.Ordinal))
        {
            return SavedFightOrigin.Created;
        }
        if (fileName.StartsWith(OriginPrefix(SavedFightOrigin.Imported) + "-", StringComparison.Ordinal))
        {
            return SavedFightOrigin.Imported;
        }
        return null;
    }

    private static string OriginPrefix(SavedFightOrigin origin) =>
        origin == SavedFightOrigin.Created ? "created" : "imported";

    /// <summary>Filesystem-safe title fragment: lowercase ascii alphanumerics with single dashes, max 40
    /// chars, "fight" when nothing survives. Purely cosmetic — identity lives in the timestamp+suffix.</summary>
    private static string Slugify(string title)
    {
        StringBuilder slug = new();
        bool lastWasDash = false;
        foreach (char c in title.Trim().ToLowerInvariant())
        {
            if (slug.Length >= 40)
            {
                break;
            }
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                slug.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash && slug.Length > 0)
            {
                slug.Append('-');
                lastWasDash = true;
            }
        }

        string result = slug.ToString().TrimEnd('-');
        return result.Length == 0 ? "fight" : result;
    }
}
