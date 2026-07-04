using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>
/// The shareable fight format (CLAUDE.md §12b) — the JSON/paste-code shape of one captured fight.
/// Built from a <see cref="DojoFightSnapshot"/> plus metadata; consumed by the import path as
/// <see cref="ToLaunchOptions"/> + the loadout lists. Reuses the game's own serializable DTOs
/// (cards/relics/potions/RNG sets) so relic/card Props restore through the identical SavedProperties
/// pipeline the game uses for quit/resume. Field names are snake_case via JsonPropertyName, matching
/// the rest of this mod's save-adjacent formats. Kept free of any game-scene/Godot dependency so the
/// §5b test harness can compile it against DTO doubles.
/// </summary>
public sealed class SharedFightPayload
{
    /// <summary>Bump whenever this payload shape changes incompatibly. Gated exactly on import
    /// (§12c, revised 2026-07-04): schema mismatch refuses, mod version does not.</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("payload_schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Game build the fight was captured on (`ReleaseInfoManager` version, the same string the
    /// game stamps into `.run` files as build_id). Exact-match gated on import (§12c).</summary>
    [JsonPropertyName("game_build_id")]
    public string GameBuildId { get; set; } = string.Empty;

    /// <summary>Mod version at capture time. Diagnostics only — deliberately NOT gated (§12c).</summary>
    [JsonPropertyName("mod_version")]
    public string ModVersion { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("created_utc")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("character")]
    public ModelId? CharacterId { get; set; }

    [JsonPropertyName("ascension")]
    public int Ascension { get; set; }

    [JsonPropertyName("encounter")]
    public ModelId? EncounterId { get; set; }

    [JsonPropertyName("seed")]
    public string Seed { get; set; } = string.Empty;

    [JsonPropertyName("run_rng")]
    public SerializableRunRngSet? RunRng { get; set; }

    [JsonPropertyName("player_rng")]
    public SerializablePlayerRngSet? PlayerRng { get; set; }

    [JsonPropertyName("deck")]
    public List<SerializableCard> Deck { get; set; } = [];

    [JsonPropertyName("relics")]
    public List<SerializableRelic> Relics { get; set; } = [];

    [JsonPropertyName("potions")]
    public List<SerializablePotion> Potions { get; set; } = [];

    [JsonPropertyName("max_potion_slots")]
    public int MaxPotionSlots { get; set; }

    [JsonPropertyName("current_hp")]
    public int CurrentHp { get; set; }

    [JsonPropertyName("max_hp")]
    public int MaxHp { get; set; }

    [JsonPropertyName("gold")]
    public int Gold { get; set; }

    public static SharedFightPayload FromSnapshot(
        DojoFightSnapshot snapshot,
        string gameBuildId,
        string modVersion,
        string title,
        string comment,
        DateTime createdUtc)
    {
        return new SharedFightPayload
        {
            SchemaVersion = CurrentSchemaVersion,
            GameBuildId = gameBuildId,
            ModVersion = modVersion,
            Title = title,
            Comment = comment,
            CreatedUtc = createdUtc,
            CharacterId = snapshot.CharacterId,
            Ascension = snapshot.Ascension,
            EncounterId = snapshot.EncounterId,
            Seed = snapshot.Seed,
            RunRng = snapshot.RunRng,
            PlayerRng = snapshot.PlayerRng,
            Deck = snapshot.Deck,
            Relics = snapshot.Relics,
            Potions = snapshot.Potions,
            MaxPotionSlots = snapshot.MaxPotionSlots,
            CurrentHp = snapshot.CurrentHp,
            MaxHp = snapshot.MaxHp,
            Gold = snapshot.Gold,
        };
    }

    /// <summary>The launch-side half of the payload: construct the throwaway run with the captured seed
    /// string (mandatory — RunState.Rng is init-only and both LoadFromSerializable methods throw on a
    /// seed mismatch) and reconcile stream counters to the captured values after the mutate callback.</summary>
    public DojoLaunchOptions ToLaunchOptions() => new()
    {
        SeedOverride = Seed,
        RunRngCounters = RunRng,
        PlayerRngCounters = PlayerRng,
    };

    /// <summary>Structural sanity check, run by the codec on every decode: catches truncated/hand-edited
    /// payloads with a clear message instead of a downstream NRE or the RNG seed-mismatch throw. Distinct
    /// from the §12c compatibility gate (build/schema) and the §6 content-eligibility gate (id resolve) —
    /// this only asserts the payload is internally complete and consistent.</summary>
    public IReadOnlyList<string> GetStructuralProblems()
    {
        List<string> problems = [];
        if (string.IsNullOrWhiteSpace(Seed))
        {
            problems.Add("missing seed");
        }
        if (string.IsNullOrWhiteSpace(GameBuildId))
        {
            problems.Add("missing game build id");
        }
        if (CharacterId == null)
        {
            problems.Add("missing character");
        }
        if (EncounterId == null)
        {
            problems.Add("missing encounter");
        }
        if (RunRng == null)
        {
            problems.Add("missing run RNG counters");
        }
        else if (RunRng.Seed != Seed)
        {
            problems.Add("run RNG seed does not match the fight seed");
        }
        if (PlayerRng == null)
        {
            problems.Add("missing player RNG counters");
        }
        if (Deck.Count == 0)
        {
            problems.Add("empty deck");
        }
        if (Deck.Any(c => c?.Id == null))
        {
            problems.Add("deck contains a card with no id");
        }
        if (Relics.Any(r => r?.Id == null))
        {
            problems.Add("relic list contains a relic with no id");
        }
        if (Potions.Any(p => p?.Id == null))
        {
            problems.Add("potion list contains a potion with no id");
        }
        if (MaxHp <= 0 || CurrentHp <= 0 || CurrentHp > MaxHp)
        {
            problems.Add($"invalid HP {CurrentHp}/{MaxHp}");
        }
        return problems;
    }
}
