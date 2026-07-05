using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

public enum DojoContentKind
{
    Encounter,
    Monster,
    Card,
    Relic,
    Potion,

    /// <summary>Only checked on the §12 shared-fight import path: a run-history replay's character is
    /// by definition installed (the run was played here), but an imported fight can name a character
    /// from a mod the importer doesn't have.</summary>
    Character
}

public interface IDojoContentResolver
{
    bool CanResolve(ModelId id, DojoContentKind kind);
}

public readonly record struct MissingDojoContent(ModelId Id, DojoContentKind Kind);

public sealed class DojoContentEligibilityResult
{
    public required IReadOnlyList<MissingDojoContent> MissingContent { get; init; }

    public bool IsEligible => MissingContent.Count == 0;
}

/// <summary>Thrown to abort a Dojo replay launch when <see cref="DojoContentEligibility.Validate"/> finds
/// content the reconstructed loadout depends on that no longer resolves (e.g. an encounter/card/relic
/// renamed or removed since the source run was played, or content from an uninstalled mod — CLAUDE.md §6).
/// Intentionally thrown from inside a <c>DojoLaunch</c> mutate callback, before any player mutation, so the
/// existing mutate-failure recovery path (<c>RunManager.CleanUp()</c>, no scene ever created) handles
/// teardown without extra plumbing.</summary>
public sealed class DojoContentEligibilityException(IReadOnlyList<MissingDojoContent> missingContent)
    : Exception(BuildMessage(missingContent))
{
    public IReadOnlyList<MissingDojoContent> MissingContent { get; } = missingContent;

    private static string BuildMessage(IReadOnlyList<MissingDojoContent> missingContent) =>
        "Dojo replay is ineligible - missing content: " +
        string.Join(", ", missingContent.Select(m => $"{m.Kind}:{m.Id}"));
}

public static class DojoContentEligibility
{
    public static DojoContentEligibilityResult Validate(
        ReconstructedLoadout loadout, IDojoContentResolver resolver)
    {
        return Validate(
            resolver,
            loadout.EncounterId,
            monsterIds: loadout.MonsterIds,
            cardIds: loadout.Deck.Select(c => c.Card.Id).OfType<ModelId>(),
            relicIds: loadout.Relics.Select(r => r.Relic.Id).OfType<ModelId>(),
            potionIds: loadout.Potions.Select(p => p.PotionId));
    }

    /// <summary>Id-list overload for callers without a <see cref="ReconstructedLoadout"/> — the §12
    /// shared-fight import path validates a decoded payload's ids through this (which also checks the
    /// character, unlike the replay path — see <see cref="DojoContentKind.Character"/>).</summary>
    public static DojoContentEligibilityResult Validate(
        IDojoContentResolver resolver,
        ModelId? encounterId,
        ModelId? characterId = null,
        IEnumerable<ModelId>? monsterIds = null,
        IEnumerable<ModelId>? cardIds = null,
        IEnumerable<ModelId>? relicIds = null,
        IEnumerable<ModelId>? potionIds = null)
    {
        List<MissingDojoContent> missing = [];

        if (characterId != null)
        {
            AddIfMissing(characterId, DojoContentKind.Character, resolver, missing);
        }
        if (encounterId != null)
        {
            AddIfMissing(encounterId, DojoContentKind.Encounter, resolver, missing);
        }
        foreach (ModelId monsterId in monsterIds ?? [])
        {
            AddIfMissing(monsterId, DojoContentKind.Monster, resolver, missing);
        }
        foreach (ModelId cardId in cardIds ?? [])
        {
            AddIfMissing(cardId, DojoContentKind.Card, resolver, missing);
        }
        foreach (ModelId relicId in relicIds ?? [])
        {
            AddIfMissing(relicId, DojoContentKind.Relic, resolver, missing);
        }
        foreach (ModelId potionId in potionIds ?? [])
        {
            AddIfMissing(potionId, DojoContentKind.Potion, resolver, missing);
        }

        return new DojoContentEligibilityResult { MissingContent = missing };
    }

    private static void AddIfMissing(
        ModelId id,
        DojoContentKind kind,
        IDojoContentResolver resolver,
        ICollection<MissingDojoContent> missing)
    {
        if (!resolver.CanResolve(id, kind))
        {
            missing.Add(new MissingDojoContent(id, kind));
        }
    }
}
