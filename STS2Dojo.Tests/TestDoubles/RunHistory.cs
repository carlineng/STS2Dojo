using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs.History;

namespace MegaCrit.Sts2.Core.Runs;

public sealed class RunHistory
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("build_id")]
    public string BuildId { get; set; } = "";

    [JsonPropertyName("ascension")]
    public int Ascension { get; set; }

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
}
