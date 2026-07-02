using System.Text.Json.Serialization;

namespace MegaCrit.Sts2.Core.Localization;

/// <summary>Minimal double of the real LocString — just enough shape (LocEntryKey) for
/// RunReconstructor's event-name-prefix check (event_choices[].title.key's prefix before ".pages.").</summary>
public sealed class LocString
{
    [JsonPropertyName("table")]
    public string LocTable { get; set; } = "";

    [JsonPropertyName("key")]
    public string LocEntryKey { get; set; } = "";
}
