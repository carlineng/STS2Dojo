using System;
using System.Linq;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

public static class RealProfilePath
{
    public static string BuildProfileBasePath(string steamId, int profileNumber)
    {
        if (string.IsNullOrWhiteSpace(steamId))
        {
            throw new ArgumentException("Steam id is required.", nameof(steamId));
        }
        if (steamId.Any(c => c is '/' or '\\'))
        {
            throw new ArgumentException("Steam id must not contain path separators.", nameof(steamId));
        }
        if (profileNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(profileNumber), "Profile number is 1-based.");
        }

        return $"user://steam/{steamId}/profile{profileNumber}";
    }

    public static string BuildHistoryPath(string steamId, int profileNumber) =>
        BuildProfileBasePath(steamId, profileNumber) + "/saves/history";

    public static string BuildHistoryPathFromProfileBasePath(string profileBasePath)
    {
        const string moddedSegment = "/modded/";
        string normalized = profileBasePath.TrimEnd('/');
        int moddedIndex = normalized.IndexOf(moddedSegment, StringComparison.Ordinal);
        if (moddedIndex >= 0)
        {
            normalized = normalized.Remove(moddedIndex, "/modded".Length);
        }

        return normalized + "/saves/history";
    }
}
