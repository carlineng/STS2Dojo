using System.Text.Json;
using System.Text.Json.Serialization;

namespace MegaCrit.Sts2.Core.Models;

[JsonConverter(typeof(ModelIdJsonConverter))]
public sealed record ModelId(string Category, string Entry) : IComparable<ModelId>
{
    public static ModelId Deserialize(string value)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 2)
        {
            throw new JsonException("'" + value + "' does not match CATEGORY.ENTRY ModelId form.");
        }

        return new ModelId(parts[0], parts[1]);
    }

    public override string ToString() => Category + "." + Entry;

    public int CompareTo(ModelId? other)
    {
        int category = string.Compare(Category, other?.Category, StringComparison.Ordinal);
        return category != 0 ? category : string.Compare(Entry, other?.Entry, StringComparison.Ordinal);
    }
}

public sealed class ModelIdJsonConverter : JsonConverter<ModelId>
{
    public override ModelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ModelId.Deserialize(reader.GetString() ?? throw new JsonException("ModelId cannot be null."));

    public override void Write(Utf8JsonWriter writer, ModelId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
