using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace MegaCrit.Sts2.Core.Runs;

public sealed class RunHistory
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("build_id")]
    public string BuildId { get; set; } = "";

    [JsonPropertyName("ascension")]
    public int Ascension { get; set; }

    [JsonPropertyName("win")]
    public bool Win { get; set; }

    [JsonPropertyName("was_abandoned")]
    public bool WasAbandoned { get; set; }

    [JsonPropertyName("seed")]
    public string Seed { get; set; } = "";

    [JsonPropertyName("start_time")]
    public long StartTime { get; set; }

    [JsonPropertyName("run_time")]
    public float RunTime { get; set; }

    [JsonPropertyName("killed_by_encounter")]
    public ModelId? KilledByEncounter { get; set; }

    [JsonPropertyName("killed_by_event")]
    public ModelId? KilledByEvent { get; set; }

    [JsonPropertyName("acts")]
    public List<ModelId> Acts { get; set; } = [];

    [JsonPropertyName("modifiers")]
    public List<object> Modifiers { get; set; } = [];

    [JsonPropertyName("players")]
    public List<RunHistoryPlayer> Players { get; set; } = [];

    [JsonPropertyName("map_point_history")]
    public List<List<MapPointHistoryEntry>> MapPointHistory { get; set; } = [];
}

public sealed class RunHistoryPlayer
{
    [JsonPropertyName("id")]
    public ulong Id { get; set; }

    [JsonPropertyName("character")]
    public ModelId Character { get; set; } = ModelId.Deserialize("CHARACTER.UNKNOWN");

    [JsonPropertyName("deck")]
    public List<SerializableCard> Deck { get; set; } = [];

    [JsonPropertyName("relics")]
    public List<SerializableRelic> Relics { get; set; } = [];
}
