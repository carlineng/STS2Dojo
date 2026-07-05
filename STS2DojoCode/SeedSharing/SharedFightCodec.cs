using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>Any user-facing decode failure: malformed paste, truncated file, wrong prefix, structurally
/// incomplete payload. The Message is always presentable text (§12e: "clear inline error, never a raw
/// exception/stack trace") — callers show it verbatim.</summary>
public sealed class SharedFightFormatException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Encodes/decodes <see cref="SharedFightPayload"/> in the two §12d transports, which are ONE format:
/// canonical JSON (the file form, human-readable), and a compact paste-code that is just
/// <c>STS2DOJO1.</c> + base64(gzip(the same JSON, unindented)). The §12d "open consideration" is
/// resolved single-format: no second PacketWriter serializer to keep in sync.
///
/// ⚠️ The JSON layer is HAND-ROLLED over <see cref="Utf8JsonWriter"/>/<see cref="JsonDocument"/> on
/// purpose — do not "simplify" it back to <c>JsonSerializer</c>. The first build used a reflection
/// resolver (<c>DefaultJsonTypeInfoResolver</c> + runtime-registered converters) and crashed in-game
/// (2026-07-04): the game ships a self-contained TRIMMED .NET runtime ("Undefined resource string ID"
/// exceptions), where reflection-based System.Text.Json setup is stripped/disabled. Writer/reader
/// primitives are the one S.T.J surface guaranteed alive (the game's own source-gen save pipeline sits
/// on them), and the §5b test harness locks this file's exact wire format (snake_case names, snake_case
/// enum counter keys, ModelId as "CATEGORY.ENTRY" strings, SavedProperties' name/value field shape —
/// verified against real `.run` props JSON).
/// </summary>
public static class SharedFightCodec
{
    /// <summary>Transport prefix; the digit is the TRANSPORT version (prefix+compression framing), not
    /// the payload schema version — those evolve independently.</summary>
    public const string CodePrefix = "STS2DOJO1.";

    private const string CodeFamilyMarker = "STS2DOJO";

    // ------------------------------------------------------------------ public API

    public static string ToJson(SharedFightPayload payload) => Serialize(payload, indented: true);

    public static SharedFightPayload FromJson(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new SharedFightFormatException(
                "This doesn't look like a valid exported fight (unreadable JSON).", e);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new SharedFightFormatException(
                    "This doesn't look like a valid exported fight (empty JSON).");
            }

            SharedFightPayload payload;
            try
            {
                payload = ReadPayload(document.RootElement);
            }
            catch (Exception e) when (
                e is JsonException or InvalidOperationException or FormatException or ArgumentException)
            {
                throw new SharedFightFormatException(
                    "This exported fight is incomplete or damaged (a field has the wrong shape).", e);
            }

            IReadOnlyList<string> problems = payload.GetStructuralProblems();
            if (problems.Count > 0)
            {
                throw new SharedFightFormatException(
                    "This exported fight is incomplete or damaged: " + string.Join("; ", problems) + ".");
            }

            return payload;
        }
    }

    public static string ToCode(SharedFightPayload payload)
    {
        byte[] json = Encoding.UTF8.GetBytes(Serialize(payload, indented: false));
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(json, 0, json.Length);
        }
        return CodePrefix + Convert.ToBase64String(compressed.ToArray());
    }

    public static SharedFightPayload FromCode(string code)
    {
        // Pastes routinely pick up whitespace/newlines from chat clients — strip all of it up front.
        string cleaned = new(code.Where(c => !char.IsWhiteSpace(c)).ToArray());

        if (!cleaned.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SharedFightFormatException(
                cleaned.StartsWith(CodeFamilyMarker, StringComparison.OrdinalIgnoreCase)
                    ? "This fight code was made by a different version of the Dojo mod and can't be read."
                    : "This doesn't look like an exported fight code (expected it to start with " +
                      CodePrefix + ").");
        }

        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(cleaned[CodePrefix.Length..]);
        }
        catch (FormatException e)
        {
            throw new SharedFightFormatException(
                "This fight code is damaged (not valid base64) — make sure the whole code was copied.", e);
        }

        string json;
        try
        {
            using MemoryStream input = new(compressed);
            using GZipStream gzip = new(input, CompressionMode.Decompress);
            using StreamReader reader = new(gzip, Encoding.UTF8);
            json = reader.ReadToEnd();
        }
        catch (InvalidDataException e)
        {
            throw new SharedFightFormatException(
                "This fight code is damaged (corrupt data) — make sure the whole code was copied.", e);
        }

        return FromJson(json);
    }

    /// <summary>The §12e paste-box entry point: accepts either transport — raw payload JSON (a pasted or
    /// drag-dropped file's contents) or a compact code.</summary>
    public static SharedFightPayload Parse(string pasted)
    {
        string trimmed = pasted?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new SharedFightFormatException("Paste an exported fight code (or file contents) first.");
        }

        return trimmed.StartsWith('{') ? FromJson(trimmed) : FromCode(trimmed);
    }

    // ------------------------------------------------------------------ writing

    private static string Serialize(SharedFightPayload payload, bool indented)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = indented }))
        {
            WritePayload(writer, payload);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WritePayload(Utf8JsonWriter writer, SharedFightPayload payload)
    {
        writer.WriteStartObject();
        writer.WriteNumber("payload_schema_version", payload.SchemaVersion);
        writer.WriteString("game_build_id", payload.GameBuildId);
        writer.WriteString("mod_version", payload.ModVersion);
        writer.WriteString("title", payload.Title);
        writer.WriteString("comment", payload.Comment);
        writer.WriteString("author", payload.Author);
        // "O" (round-trip) format: preserves ticks and DateTimeKind exactly, culture-independent.
        writer.WriteString("created_utc", payload.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        WriteModelId(writer, "character", payload.CharacterId);
        writer.WriteNumber("ascension", payload.Ascension);
        WriteModelId(writer, "encounter", payload.EncounterId);
        writer.WriteString("seed", payload.Seed);

        if (payload.RunRng != null)
        {
            writer.WriteStartObject("run_rng");
            writer.WriteString("seed", payload.RunRng.Seed);
            writer.WriteStartObject("counters");
            foreach (RunRngType type in Enum.GetValues<RunRngType>())
            {
                if (payload.RunRng.Counters.TryGetValue(type, out int counter))
                {
                    writer.WriteNumber(EnumNames<RunRngType>.ToSnake[type], counter);
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (payload.PlayerRng != null)
        {
            writer.WriteStartObject("player_rng");
            writer.WriteNumber("seed", payload.PlayerRng.Seed);
            writer.WriteStartObject("counters");
            foreach (PlayerRngType type in Enum.GetValues<PlayerRngType>())
            {
                if (payload.PlayerRng.Counters.TryGetValue(type, out int counter))
                {
                    writer.WriteNumber(EnumNames<PlayerRngType>.ToSnake[type], counter);
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteStartArray("deck");
        foreach (SerializableCard card in payload.Deck)
        {
            WriteCard(writer, card);
        }
        writer.WriteEndArray();

        writer.WriteStartArray("relics");
        foreach (SerializableRelic relic in payload.Relics)
        {
            writer.WriteStartObject();
            WriteModelId(writer, "id", relic.Id);
            WriteProps(writer, relic.Props);
            if (relic.FloorAddedToDeck.HasValue)
            {
                writer.WriteNumber("floor_added_to_deck", relic.FloorAddedToDeck.Value);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("potions");
        foreach (SerializablePotion potion in payload.Potions)
        {
            writer.WriteStartObject();
            WriteModelId(writer, "id", potion.Id);
            writer.WriteNumber("slot_index", potion.SlotIndex);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteNumber("max_potion_slots", payload.MaxPotionSlots);
        writer.WriteNumber("current_hp", payload.CurrentHp);
        writer.WriteNumber("max_hp", payload.MaxHp);
        writer.WriteNumber("gold", payload.Gold);
        writer.WriteEndObject();
    }

    private static void WriteCard(Utf8JsonWriter writer, SerializableCard card)
    {
        writer.WriteStartObject();
        WriteModelId(writer, "id", card.Id);
        if (card.CurrentUpgradeLevel != 0)
        {
            writer.WriteNumber("current_upgrade_level", card.CurrentUpgradeLevel);
        }
        if (card.Enchantment != null)
        {
            writer.WriteStartObject("enchantment");
            WriteModelId(writer, "id", card.Enchantment.Id);
            writer.WriteNumber("amount", card.Enchantment.Amount);
            writer.WriteEndObject();
        }
        WriteProps(writer, card.Props);
        if (card.FloorAddedToDeck.HasValue)
        {
            writer.WriteNumber("floor_added_to_deck", card.FloorAddedToDeck.Value);
        }
        writer.WriteEndObject();
    }

    /// <summary>Same shape the game writes into `.run`/save files: optional per-type lists of
    /// {"name": ..., "value": ...} pairs, absent when null.</summary>
    private static void WriteProps(Utf8JsonWriter writer, SavedProperties? props)
    {
        if (props == null)
        {
            return;
        }

        writer.WriteStartObject("props");
        WritePropList(writer, "ints", props.ints, static (w, v) => w.WriteNumberValue(v));
        WritePropList(writer, "bools", props.bools, static (w, v) => w.WriteBooleanValue(v));
        WritePropList(writer, "strings", props.strings, static (w, v) => w.WriteStringValue(v));
        WritePropList(writer, "int_arrays", props.intArrays, static (w, v) =>
        {
            w.WriteStartArray();
            foreach (int item in v)
            {
                w.WriteNumberValue(item);
            }
            w.WriteEndArray();
        });
        WritePropList(writer, "model_ids", props.modelIds, static (w, v) => w.WriteStringValue(v.ToString()));
        WritePropList(writer, "cards", props.cards, static (w, v) => WriteCard(w, v));
        WritePropList(writer, "card_arrays", props.cardArrays, static (w, v) =>
        {
            w.WriteStartArray();
            foreach (SerializableCard item in v)
            {
                WriteCard(w, item);
            }
            w.WriteEndArray();
        });
        writer.WriteEndObject();
    }

    private static void WritePropList<T>(
        Utf8JsonWriter writer,
        string name,
        List<SavedProperties.SavedProperty<T>>? list,
        Action<Utf8JsonWriter, T> writeValue)
    {
        if (list == null)
        {
            return;
        }

        writer.WriteStartArray(name);
        foreach (SavedProperties.SavedProperty<T> property in list)
        {
            writer.WriteStartObject();
            writer.WriteString("name", property.name);
            writer.WritePropertyName("value");
            writeValue(writer, property.value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteModelId(Utf8JsonWriter writer, string name, ModelId? id)
    {
        if (id != null)
        {
            writer.WriteString(name, id.ToString());
        }
    }

    // ------------------------------------------------------------------ reading

    private static SharedFightPayload ReadPayload(JsonElement root)
    {
        var payload = new SharedFightPayload
        {
            SchemaVersion = ReadInt(root, "payload_schema_version"),
            GameBuildId = ReadString(root, "game_build_id"),
            ModVersion = ReadString(root, "mod_version"),
            Title = ReadString(root, "title"),
            Comment = ReadString(root, "comment"),
            Author = ReadString(root, "author"),
            CharacterId = ReadModelId(root, "character"),
            Ascension = ReadInt(root, "ascension"),
            EncounterId = ReadModelId(root, "encounter"),
            Seed = ReadString(root, "seed"),
            MaxPotionSlots = ReadInt(root, "max_potion_slots"),
            CurrentHp = ReadInt(root, "current_hp"),
            MaxHp = ReadInt(root, "max_hp"),
            Gold = ReadInt(root, "gold"),
        };

        if (TryGetProperty(root, "created_utc", JsonValueKind.String, out JsonElement created))
        {
            payload.CreatedUtc = DateTime.Parse(
                created.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (TryGetProperty(root, "run_rng", JsonValueKind.Object, out JsonElement runRng))
        {
            var set = new SerializableRunRngSet { Seed = ReadString(runRng, "seed") };
            ReadCounters(runRng, set.Counters, EnumNames<RunRngType>.FromSnake);
            payload.RunRng = set;
        }

        if (TryGetProperty(root, "player_rng", JsonValueKind.Object, out JsonElement playerRng))
        {
            var set = new SerializablePlayerRngSet();
            if (TryGetProperty(playerRng, "seed", JsonValueKind.Number, out JsonElement seed))
            {
                set.Seed = seed.GetUInt32();
            }
            ReadCounters(playerRng, set.Counters, EnumNames<PlayerRngType>.FromSnake);
            payload.PlayerRng = set;
        }

        if (TryGetProperty(root, "deck", JsonValueKind.Array, out JsonElement deck))
        {
            payload.Deck = deck.EnumerateArray().Select(ReadCard).ToList();
        }

        if (TryGetProperty(root, "relics", JsonValueKind.Array, out JsonElement relics))
        {
            payload.Relics = relics.EnumerateArray().Select(element => new SerializableRelic
            {
                Id = ReadModelId(element, "id"),
                Props = ReadProps(element),
                FloorAddedToDeck = ReadOptionalInt(element, "floor_added_to_deck"),
            }).ToList();
        }

        if (TryGetProperty(root, "potions", JsonValueKind.Array, out JsonElement potions))
        {
            payload.Potions = potions.EnumerateArray().Select(element => new SerializablePotion
            {
                Id = ReadModelId(element, "id"),
                SlotIndex = ReadInt(element, "slot_index"),
            }).ToList();
        }

        return payload;
    }

    private static void ReadCounters<T>(
        JsonElement parent, Dictionary<T, int> counters, IReadOnlyDictionary<string, T> fromSnake)
        where T : struct, Enum
    {
        if (!TryGetProperty(parent, "counters", JsonValueKind.Object, out JsonElement element))
        {
            return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            // Unknown stream names are skipped, not errors: they can only come from a different game
            // build, and the §12c build gate produces the better refusal message for that case.
            if (fromSnake.TryGetValue(property.Name, out T type) &&
                property.Value.ValueKind == JsonValueKind.Number)
            {
                counters[type] = property.Value.GetInt32();
            }
        }
    }

    private static SerializableCard ReadCard(JsonElement element)
    {
        var card = new SerializableCard
        {
            Id = ReadModelId(element, "id"),
            CurrentUpgradeLevel = ReadInt(element, "current_upgrade_level"),
            Props = ReadProps(element),
            FloorAddedToDeck = ReadOptionalInt(element, "floor_added_to_deck"),
        };

        if (TryGetProperty(element, "enchantment", JsonValueKind.Object, out JsonElement enchantment))
        {
            card.Enchantment = new SerializableEnchantment
            {
                Id = ReadModelId(enchantment, "id"),
                Amount = ReadInt(enchantment, "amount"),
            };
        }

        return card;
    }

    private static SavedProperties? ReadProps(JsonElement parent)
    {
        if (!TryGetProperty(parent, "props", JsonValueKind.Object, out JsonElement element))
        {
            return null;
        }

        var props = new SavedProperties
        {
            ints = ReadPropList(element, "ints", static v => v.GetInt32()),
            bools = ReadPropList(element, "bools", static v => v.GetBoolean()),
            strings = ReadPropList(element, "strings", static v => v.GetString() ?? string.Empty),
            intArrays = ReadPropList(element, "int_arrays",
                static v => v.EnumerateArray().Select(item => item.GetInt32()).ToArray()),
            modelIds = ReadPropList(element, "model_ids",
                static v => ModelId.Deserialize(v.GetString() ?? string.Empty)),
            cards = ReadPropList(element, "cards", ReadCard),
            cardArrays = ReadPropList(element, "card_arrays",
                v => v.EnumerateArray().Select(ReadCard).ToArray()),
        };
        return props;
    }

    private static List<SavedProperties.SavedProperty<T>>? ReadPropList<T>(
        JsonElement parent, string name, Func<JsonElement, T> readValue)
    {
        if (!TryGetProperty(parent, name, JsonValueKind.Array, out JsonElement element))
        {
            return null;
        }

        List<SavedProperties.SavedProperty<T>> list = [];
        foreach (JsonElement entry in element.EnumerateArray())
        {
            string propertyName = ReadString(entry, "name");
            if (entry.TryGetProperty("value", out JsonElement value))
            {
                list.Add(new SavedProperties.SavedProperty<T>(propertyName, readValue(value)));
            }
        }
        return list;
    }

    private static bool TryGetProperty(
        JsonElement parent, string name, JsonValueKind kind, out JsonElement element)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out element) && element.ValueKind == kind)
        {
            return true;
        }
        element = default;
        return false;
    }

    private static string ReadString(JsonElement parent, string name, string fallback = "") =>
        TryGetProperty(parent, name, JsonValueKind.String, out JsonElement element)
            ? element.GetString() ?? fallback
            : fallback;

    private static int ReadInt(JsonElement parent, string name, int fallback = 0) =>
        TryGetProperty(parent, name, JsonValueKind.Number, out JsonElement element)
            ? element.GetInt32()
            : fallback;

    private static int? ReadOptionalInt(JsonElement parent, string name) =>
        TryGetProperty(parent, name, JsonValueKind.Number, out JsonElement element)
            ? element.GetInt32()
            : null;

    private static ModelId? ReadModelId(JsonElement parent, string name) =>
        TryGetProperty(parent, name, JsonValueKind.String, out JsonElement element)
            ? ModelId.Deserialize(element.GetString() ?? string.Empty)
            : null;

    /// <summary>Bidirectional enum-name ↔ snake_case maps, built once per enum. Matches how the wire
    /// format has always snake-cased these members ("UpFront" → "up_front"); simple upper-boundary
    /// snake-casing is exact for every current RunRngType/PlayerRngType member (locked by tests).</summary>
    private static class EnumNames<T> where T : struct, Enum
    {
        public static readonly Dictionary<T, string> ToSnake = new();
        public static readonly Dictionary<string, T> FromSnake = new();

        static EnumNames()
        {
            foreach (T value in Enum.GetValues<T>())
            {
                string snake = SnakeCase(value.ToString());
                ToSnake[value] = snake;
                FromSnake[snake] = value;
            }
        }

        private static string SnakeCase(string name)
        {
            StringBuilder result = new(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0)
                    {
                        result.Append('_');
                    }
                    result.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    result.Append(c);
                }
            }
            return result.ToString();
        }
    }
}
