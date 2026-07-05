using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>
/// Optional per-launch overrides for <see cref="DojoLaunch"/>, added for repeatable-seed fight sharing
/// (CLAUDE.md §12). The default instance preserves the pre-§12 behavior exactly: fresh random seed per
/// attempt, no counter restore.
/// </summary>
public sealed record DojoLaunchOptions
{
    public static readonly DojoLaunchOptions Default = new();

    /// <summary>Exact seed string for the throwaway run. Null → a fresh random seed per attempt (the
    /// deliberate §3 fresh-RNG design for normal Dojo fights). Imported shared fights set this to the
    /// payload's seed; because the whole options object is retained in <c>DojoLaunch</c>'s LaunchRequest,
    /// Try Again then replays the identical seed too (§12 decision 2026-07-04). Must be set whenever
    /// either counter set below is set — the game's <c>LoadFromSerializable</c> throws on seed mismatch.</summary>
    public string? SeedOverride { get; init; }

    /// <summary>Run-stream counters to restore right after the mutate callback (import path only).
    /// Reconciles any drift between the exporter's and importer's pre-combat stream consumption; with
    /// the same seed and the same launch sequence this is usually a no-op, kept as belt-and-braces.</summary>
    public SerializableRunRngSet? RunRngCounters { get; init; }

    /// <summary>Player-stream counters to restore alongside <see cref="RunRngCounters"/>. The player
    /// seed is itself derived from the run string seed (Player.InitializeSeed), so with
    /// <see cref="SeedOverride"/> set the seeds always match.</summary>
    public SerializablePlayerRngSet? PlayerRngCounters { get; init; }
}

/// <summary>
/// Everything §12b's shareable payload needs about one prepared fight, read off the live
/// <c>RunState</c> immediately after <c>DojoLaunch</c>'s mutate callback (and any counter restore)
/// finished — the §12a capture point. At that moment every combat-relevant RNG stream is still at its
/// pre-combat counter and the player carries exactly the deck/relics/potions/HP/gold the fight will
/// start with (including any Replay Setup adjustments, already baked into the relic/card Props by the
/// game's own SavedProperties pipeline). Captured for EVERY throwaway launch so the Completion screen
/// can offer Export after the fact, win or lose (§12a entry point 1).
/// </summary>
public sealed class DojoFightSnapshot
{
    public required string Seed { get; init; }
    public required SerializableRunRngSet RunRng { get; init; }
    public required SerializablePlayerRngSet PlayerRng { get; init; }
    public required ModelId CharacterId { get; init; }
    public required int Ascension { get; init; }
    public required ModelId EncounterId { get; init; }
    public required List<SerializableCard> Deck { get; init; }
    public required List<SerializableRelic> Relics { get; init; }
    public required List<SerializablePotion> Potions { get; init; }
    public required int MaxPotionSlots { get; init; }
    public required int CurrentHp { get; init; }
    public required int MaxHp { get; init; }
    public required int Gold { get; init; }
}
