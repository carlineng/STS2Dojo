using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace MegaCrit.Sts2.Core.Runs.History;

public sealed class MapPointHistoryEntry
{
    [JsonPropertyName("rooms")]
    public List<MapPointRoomHistoryEntry> Rooms { get; set; } = [];

    [JsonPropertyName("player_stats")]
    public List<PlayerMapPointHistoryEntry> PlayerStats { get; set; } = [];
}

public sealed class MapPointRoomHistoryEntry
{
    [JsonPropertyName("room_type")]
    public RoomType RoomType { get; set; }

    [JsonPropertyName("model_id")]
    public ModelId? ModelId { get; set; }

    [JsonPropertyName("monster_ids")]
    public List<ModelId> MonsterIds { get; set; } = [];
}

public sealed class PlayerMapPointHistoryEntry
{
    [JsonPropertyName("player_id")]
    public ulong PlayerId { get; set; }

    [JsonPropertyName("current_hp")]
    public int CurrentHp { get; set; }

    [JsonPropertyName("max_hp")]
    public int MaxHp { get; set; }

    [JsonPropertyName("current_gold")]
    public int CurrentGold { get; set; }

    [JsonPropertyName("cards_gained")]
    public List<SerializableCard> CardsGained { get; set; } = [];

    [JsonPropertyName("cards_removed")]
    public List<SerializableCard> CardsRemoved { get; set; } = [];

    [JsonPropertyName("cards_transformed")]
    public List<CardTransformationHistoryEntry> CardsTransformed { get; set; } = [];

    [JsonPropertyName("cards_enchanted")]
    public List<CardEnchantmentHistoryEntry> CardsEnchanted { get; set; } = [];

    [JsonPropertyName("upgraded_cards")]
    public List<ModelId> UpgradedCards { get; set; } = [];

    [JsonPropertyName("downgraded_cards")]
    public List<ModelId> DowngradedCards { get; set; } = [];

    [JsonPropertyName("relic_choices")]
    public List<ModelChoiceHistoryEntry> RelicChoices { get; set; } = [];

    [JsonPropertyName("relics_removed")]
    public List<ModelId> RelicsRemoved { get; set; } = [];
}

public sealed class CardTransformationHistoryEntry
{
    [JsonPropertyName("original_card")]
    public SerializableCard OriginalCard { get; set; } = new();

    [JsonPropertyName("final_card")]
    public SerializableCard FinalCard { get; set; } = new();
}

public sealed class CardEnchantmentHistoryEntry
{
    [JsonPropertyName("card")]
    public SerializableCard Card { get; set; } = new();

    [JsonPropertyName("enchantment")]
    public ModelId? Enchantment { get; set; }
}

public sealed class ModelChoiceHistoryEntry
{
    [JsonPropertyName("choice")]
    public ModelId? choice { get; set; }

    [JsonPropertyName("was_picked")]
    public bool wasPicked { get; set; }
}
