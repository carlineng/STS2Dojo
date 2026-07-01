using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Models;

namespace MegaCrit.Sts2.Core.Saves.Runs;

public sealed class SerializableCard
{
    [JsonPropertyName("id")]
    public ModelId? Id { get; set; }

    [JsonPropertyName("current_upgrade_level")]
    public int CurrentUpgradeLevel { get; set; }

    [JsonPropertyName("enchantment")]
    public SerializableEnchantment? Enchantment { get; set; }

    [JsonPropertyName("floor_added_to_deck")]
    public int? FloorAddedToDeck { get; set; }
}

public sealed class SerializableRelic
{
    [JsonPropertyName("id")]
    public ModelId? Id { get; set; }

    [JsonPropertyName("floor_added_to_deck")]
    public int? FloorAddedToDeck { get; set; }
}

public sealed class SerializableEnchantment
{
    [JsonPropertyName("id")]
    public ModelId? Id { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; }
}
