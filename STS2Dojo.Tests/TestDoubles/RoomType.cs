using System.Text.Json;
using System.Text.Json.Serialization;

namespace MegaCrit.Sts2.Core.Rooms;

[JsonConverter(typeof(RoomTypeJsonConverter))]
public enum RoomType
{
    Monster,
    Elite,
    Boss,
    Event,
    RestSite,
    Treasure,
    Shop
}

public sealed class RoomTypeJsonConverter : JsonConverter<RoomType>
{
    public override RoomType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "monster" => RoomType.Monster,
            "elite" => RoomType.Elite,
            "boss" => RoomType.Boss,
            "event" => RoomType.Event,
            "rest_site" => RoomType.RestSite,
            "treasure" => RoomType.Treasure,
            "shop" => RoomType.Shop,
            string value => throw new JsonException("Unknown room_type '" + value + "'."),
            null => throw new JsonException("room_type cannot be null.")
        };

    public override void Write(Utf8JsonWriter writer, RoomType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
