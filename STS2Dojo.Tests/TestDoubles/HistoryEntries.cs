using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Localization;
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

    [JsonPropertyName("potion_choices")]
    public List<ModelChoiceHistoryEntry> PotionChoices { get; set; } = [];

    [JsonPropertyName("bought_potions")]
    public List<ModelId> BoughtPotions { get; set; } = [];

    [JsonPropertyName("potion_used")]
    public List<ModelId> PotionUsed { get; set; } = [];

    [JsonPropertyName("potion_discarded")]
    public List<ModelId> PotionDiscarded { get; set; } = [];

    [JsonPropertyName("event_choices")]
    public List<EventOptionHistoryEntry> EventChoices { get; set; } = [];
}

public sealed class EventOptionHistoryEntry
{
    [JsonPropertyName("title")]
    public LocString Title { get; set; } = new();

    [JsonPropertyName("variables")]
    [JsonConverter(typeof(EventVariablesJsonConverter))]
    public Dictionary<string, object>? Variables { get; set; }
}

/// <summary>Mirrors the two fields RunReconstructor's reflection-based readers look for on the game's
/// real DynamicVar/StringVar types (see RunReconstructor.TryGetEventVariableString/Count) — same
/// property names, different concrete type, since the real types live in an assembly this test project
/// deliberately never references (see STS2Dojo.Tests/Program.cs's TestRunHistoryLoader notes).</summary>
public sealed class EventVariable
{
    public string? StringValue { get; set; }
    public decimal BaseValue { get; set; }
}

/// <summary>Reads the raw run-file shape (<c>{"type":..,"decimal_value":..,"bool_value":..,
/// "string_value":..}</c> per variable) directly into <see cref="EventVariable"/>, mirroring what the
/// real game's LocStringVariablesJsonConverter + SerializableDynamicVar.ToDynamicVar do together
/// (minus the Bool/plain-String/Decimal cases, which no potion event variable uses).</summary>
public sealed class EventVariablesJsonConverter : JsonConverter<Dictionary<string, object>?>
{
    public override Dictionary<string, object>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        Dictionary<string, object> result = new();
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            JsonElement value = property.Value;
            string? stringValue = value.TryGetProperty("string_value", out JsonElement sv) && sv.ValueKind == JsonValueKind.String
                ? sv.GetString()
                : null;
            decimal decimalValue = value.TryGetProperty("decimal_value", out JsonElement dv) && dv.ValueKind == JsonValueKind.Number
                ? dv.GetDecimal()
                : 0m;
            result[property.Name] = new EventVariable { StringValue = stringValue, BaseValue = decimalValue };
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, object>? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Test double is read-only.");
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
