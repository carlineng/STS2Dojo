using System.Collections.Generic;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Entities.Rngs;

// Doubles for the game's serialized RNG-set DTOs (used by the §12 shared-fight payload). Enum members
// MUST stay byte-for-byte identical to the decompiled originals — the codec serializes them (and the
// counter dictionary KEYS) as snake_case strings, so a renamed/reordered member here would let tests
// pass against a shape the real game never produces.

namespace MegaCrit.Sts2.Core.Entities.Rngs
{
    public enum RunRngType
    {
        UpFront,
        Shuffle,
        UnknownMapPoint,
        CombatCardGeneration,
        CombatPotionGeneration,
        CombatCardSelection,
        CombatEnergyCosts,
        CombatTargets,
        MonsterAi,
        Niche,
        CombatOrbs,
        TreasureRoomRelics
    }

    public enum PlayerRngType
    {
        Rewards,
        Shops,
        Transformations
    }
}

namespace MegaCrit.Sts2.Core.Saves.Runs
{
    public class SerializableRunRngSet
    {
        [JsonPropertyName("seed")]
        public string? Seed { get; set; }

        [JsonPropertyName("counters")]
        public Dictionary<RunRngType, int> Counters { get; set; } = new();
    }
}

namespace MegaCrit.Sts2.Core.Saves
{
    public class SerializablePlayerRngSet
    {
        [JsonPropertyName("seed")]
        public uint Seed { get; set; }

        [JsonPropertyName("counters")]
        public Dictionary<PlayerRngType, int> Counters { get; set; } = new();
    }
}
