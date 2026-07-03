using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MegaCrit.Sts2.Core.Models;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Live display-name lookups for the Dojo screen (the in-game implementation of the name-resolver seam
/// that <c>DojoRunListQueries</c>' search takes as a delegate). Everything is resolved from the loaded
/// <see cref="ModelDb"/> content's own localization (<c>EncounterModel.Title</c> etc.), so mod-added
/// content names itself correctly; content that no longer resolves (removed/renamed since the run was
/// played, uninstalled mod) falls back to a prettified id entry ("KNOWLEDGE_DEMON_BOSS" → "Knowledge
/// Demon") instead of throwing — those pills render greyed out anyway. Lookups are memoized: the run
/// list re-resolves names on every keystroke of the search box.
/// </summary>
public static class DojoDisplayNames
{
    private static readonly Dictionary<ModelId, string> EncounterNames = new();
    private static readonly Dictionary<ModelId, string> CharacterNames = new();
    private static readonly Dictionary<ModelId, string> ActNames = new();

    public static string Character(ModelId id) =>
        Resolve(CharacterNames, id, i => ModelDb.GetByIdOrNull<CharacterModel>(i)?.Title.GetFormattedText());

    public static string Encounter(ModelId id) =>
        Resolve(EncounterNames, id, i => ModelDb.GetByIdOrNull<EncounterModel>(i)?.Title.GetFormattedText());

    public static string Act(ModelId id) =>
        Resolve(ActNames, id, i => ModelDb.GetByIdOrNull<ActModel>(i)?.Title.GetFormattedText());

    /// <summary>The search resolver handed to DojoRunListQueries: dispatches on the id's category so one
    /// delegate covers characters and encounters.</summary>
    public static string ForSearch(ModelId id) => id.Category switch
    {
        "CHARACTER" => Character(id),
        "ENCOUNTER" => Encounter(id),
        "ACT" => Act(id),
        _ => Prettify(id.Entry)
    };

    private static string Resolve(Dictionary<ModelId, string> cache, ModelId id, Func<ModelId, string?> lookup)
    {
        if (cache.TryGetValue(id, out string? cached))
        {
            return cached;
        }

        string name;
        try
        {
            string? resolved = lookup(id);
            name = string.IsNullOrWhiteSpace(resolved) ? Prettify(id.Entry) : resolved;
        }
        catch (Exception)
        {
            name = Prettify(id.Entry);
        }

        cache[id] = name;
        return name;
    }

    /// <summary>"SKULKING_COLONY_ELITE" → "Skulking Colony". Strips the boss/elite/weak/normal role
    /// suffix (it's conveyed by the pill's placement/styling, not the name) and title-cases the rest.</summary>
    public static string Prettify(string entry)
    {
        string[] words = entry.Split('_', StringSplitOptions.RemoveEmptyEntries);
        int count = words.Length;
        if (count > 1 && words[^1] is "BOSS" or "ELITE" or "WEAK" or "NORMAL")
        {
            count--;
        }

        var result = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                result.Append(' ');
            }
            result.Append(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words[i].ToLowerInvariant()));
        }

        return result.Length > 0 ? result.ToString() : entry;
    }
}
