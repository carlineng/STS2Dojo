using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

public enum DojoVictoryFilter
{
    Both,
    Victory,
    Defeat
}

public enum DojoRunSortOrder
{
    Newest,
    Oldest,
    Floor,
    Ascension
}

/// <summary>
/// The Dojo run browser's live sidebar filters. All null/empty fields mean "no restriction".
/// Ascension is an exact match (the sidebar offers All + A0–A10 chips); Defeat includes abandoned runs
/// (anything that isn't a win).
/// </summary>
public sealed record DojoRunFilter(
    ModelId? Character = null,
    int? Ascension = null,
    DojoVictoryFilter Victory = DojoVictoryFilter.Both,
    string? SearchText = null)
{
    public static readonly DojoRunFilter None = new();
}

/// <summary>
/// Pure filtering/sorting over precomputed <see cref="DojoRunSummary"/> rows. Display names for the
/// search box (character, displayed encounter names) come through <paramref name="resolveName"/> so
/// this stays testable without ModelDb/localization — in-game the resolver wraps
/// CharacterModel.Title / EncounterModel.Title lookups (see DojoRunIndex).
/// </summary>
public static class DojoRunListQueries
{
    public static List<DojoRunSummary> Apply(
        IEnumerable<DojoRunSummary> runs,
        DojoRunFilter filter,
        DojoRunSortOrder sort,
        Func<ModelId, string> resolveName)
    {
        var resolvedNames = new Dictionary<ModelId, string>();
        string ResolveCached(ModelId id)
        {
            if (resolvedNames.TryGetValue(id, out string? name))
            {
                return name;
            }

            name = resolveName(id);
            resolvedNames[id] = name;
            return name;
        }

        IEnumerable<DojoRunSummary> result = runs.Where(run => Matches(run, filter, ResolveCached));
        return Sort(result, sort);
    }

    public static bool Matches(DojoRunSummary run, DojoRunFilter filter, Func<ModelId, string> resolveName)
    {
        if (filter.Character != null && !Equals(run.CharacterId, filter.Character))
        {
            return false;
        }

        if (filter.Ascension.HasValue && run.Ascension != filter.Ascension.Value)
        {
            return false;
        }

        switch (filter.Victory)
        {
            case DojoVictoryFilter.Victory when !run.Win:
            case DojoVictoryFilter.Defeat when run.Win:
                return false;
        }

        string? search = filter.SearchText?.Trim();
        if (string.IsNullOrEmpty(search))
        {
            return true;
        }

        return MatchesSearch(run, search, resolveName);
    }

    /// <summary>Search scope per the design: character name, boss/elite display names, relic names, deck
    /// card names, and seed. Raw encounter/relic/card ids are also matched so power users can search e.g.
    /// "AEONGLASS_BOSS" or "RELIC.PANDORAS_BOX".</summary>
    private static bool MatchesSearch(DojoRunSummary run, string search, Func<ModelId, string> resolveName)
    {
        if (Contains(run.Seed, search) || Contains(resolveName(run.CharacterId), search))
        {
            return true;
        }

        foreach (DojoActSummary act in run.Acts)
        {
            foreach (DojoFightSummary fight in act.DisplayFights)
            {
                if (Contains(resolveName(fight.DisplayId), search)
                    || Contains(resolveName(fight.EncounterId), search)
                    || Contains(fight.DisplayId.ToString(), search)
                    || Contains(fight.EncounterId.ToString(), search))
                {
                    return true;
                }
            }
        }

        foreach (ModelId relicId in run.RelicIds)
        {
            if (Contains(resolveName(relicId), search) || Contains(relicId.ToString(), search))
            {
                return true;
            }
        }

        foreach (ModelId cardId in run.DeckCardIds)
        {
            if (Contains(resolveName(cardId), search) || Contains(cardId.ToString(), search))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Newest/Oldest sort by start time; Floor and Ascension sort descending (deepest/hardest
    /// first), breaking ties by newest so the ordering is stable and predictable.</summary>
    private static List<DojoRunSummary> Sort(IEnumerable<DojoRunSummary> runs, DojoRunSortOrder sort) =>
        sort switch
        {
            DojoRunSortOrder.Oldest => runs.OrderBy(r => r.StartTime).ToList(),
            DojoRunSortOrder.Floor => runs.OrderByDescending(r => r.FloorsReached)
                .ThenByDescending(r => r.StartTime).ToList(),
            DojoRunSortOrder.Ascension => runs.OrderByDescending(r => r.Ascension)
                .ThenByDescending(r => r.StartTime).ToList(),
            _ => runs.OrderByDescending(r => r.StartTime).ToList()
        };
}
