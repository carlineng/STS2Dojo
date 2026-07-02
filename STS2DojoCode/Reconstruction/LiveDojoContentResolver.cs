using System;
using MegaCrit.Sts2.Core.Models;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

/// <summary>
/// <see cref="IDojoContentResolver"/> backed by the live, currently-loaded <see cref="ModelDb"/> (base game
/// content plus any active mods). This is the resolver <c>DojoReplayLauncher</c> validates a reconstructed
/// loadout against before launching it - see CLAUDE.md §6/§9 item 4. Not referenced by
/// <c>STS2Dojo.Tests</c>: it depends on the real <c>sts2.dll</c>, which the test harness deliberately never
/// loads (see CLAUDE.md §5b) - the pure eligibility logic in <see cref="DojoContentEligibility"/> is tested
/// there instead, against <c>FakeContentResolver</c>.
/// </summary>
public sealed class LiveDojoContentResolver : IDojoContentResolver
{
    public static readonly LiveDojoContentResolver Instance = new();

    public bool CanResolve(ModelId id, DojoContentKind kind) => kind switch
    {
        DojoContentKind.Encounter => ModelDb.GetByIdOrNull<EncounterModel>(id) != null,
        DojoContentKind.Monster => ModelDb.GetByIdOrNull<MonsterModel>(id) != null,
        DojoContentKind.Card => ModelDb.GetByIdOrNull<CardModel>(id) != null,
        DojoContentKind.Relic => ModelDb.GetByIdOrNull<RelicModel>(id) != null,
        DojoContentKind.Potion => ModelDb.GetByIdOrNull<PotionModel>(id) != null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
