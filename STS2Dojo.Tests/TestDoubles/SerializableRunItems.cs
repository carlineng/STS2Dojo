using System.Collections.Generic;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Models;

namespace MegaCrit.Sts2.Core.Saves.Runs;

public sealed class SerializableCard
{
    [JsonPropertyName("id")]
    public ModelId? Id { get; set; }

    [JsonPropertyName("current_upgrade_level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CurrentUpgradeLevel { get; set; }

    [JsonPropertyName("enchantment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SerializableEnchantment? Enchantment { get; set; }

    [JsonPropertyName("props")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SavedProperties? Props { get; set; }

    [JsonPropertyName("floor_added_to_deck")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FloorAddedToDeck { get; set; }
}

public sealed class SerializableRelic
{
    [JsonPropertyName("id")]
    public ModelId? Id { get; set; }

    [JsonPropertyName("props")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SavedProperties? Props { get; set; }

    [JsonPropertyName("floor_added_to_deck")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FloorAddedToDeck { get; set; }
}

public sealed class SerializablePotion
{
    [JsonPropertyName("id")]
    public ModelId? Id { get; set; }

    [JsonPropertyName("slot_index")]
    public int SlotIndex { get; set; }
}

/// <summary>Double mirroring the real SavedProperties' JSON surface: public FIELDS (the codec must run
/// with IncludeFields=true) holding name/value pair lists — matches the on-disk `.run` shape, e.g.
/// {"bools":[{"name":"IsUsed","value":true}]}. Reflection-only members of the real class are omitted.</summary>
public class SavedProperties
{
    public struct SavedProperty<T>(string name, T value)
    {
        public string name = name;

        public T value = value;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("ints")]
    public List<SavedProperty<int>>? ints;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("bools")]
    public List<SavedProperty<bool>>? bools;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("strings")]
    public List<SavedProperty<string>>? strings;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("int_arrays")]
    public List<SavedProperty<int[]>>? intArrays;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("model_ids")]
    public List<SavedProperty<ModelId>>? modelIds;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("cards")]
    public List<SavedProperty<SerializableCard>>? cards;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("card_arrays")]
    public List<SavedProperty<SerializableCard[]>>? cardArrays;
}

public sealed class SerializableEnchantment
{
    [JsonPropertyName("id")]
    public ModelId? Id { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; }
}
