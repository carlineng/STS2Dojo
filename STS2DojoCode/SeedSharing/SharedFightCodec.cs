using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MegaCrit.Sts2.Core.Models;

namespace STS2Dojo.STS2DojoCode.SeedSharing;

/// <summary>Any user-facing decode failure: malformed paste, truncated file, wrong prefix, structurally
/// incomplete payload. The Message is always presentable text (§12e: "clear inline error, never a raw
/// exception/stack trace") — callers show it verbatim.</summary>
public sealed class SharedFightFormatException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Encodes/decodes <see cref="SharedFightPayload"/> in the two §12d transports, which are ONE format:
/// canonical JSON (the file form, human-readable), and a compact paste-code that is just
/// <c>STS2DOJO1.</c> + base64(gzip(the same JSON, unindented)). The §12d "open consideration" is hereby
/// resolved single-format: the game's PacketWriter path would be a second hand-maintained serializer
/// that must never drift from this one, for roughly a 2-3x shorter blob that is a long paste either way.
///
/// Serializer notes (mirrors what the game's own save options do where it matters):
/// <c>IncludeFields = true</c> because SavedProperties uses public fields with lowercase names — the
/// on-disk shape (<c>{"name":...,"value":...}</c>) matches real `.run` files, verified against the
/// corpus. Enum dictionary keys (the RNG counter dicts) and enum values serialize snake_case. ModelId
/// gets an explicit converter (the game registers one at options level too; ModelId itself carries no
/// attribute).
/// </summary>
public static class SharedFightCodec
{
    /// <summary>Transport prefix; the digit is the TRANSPORT version (prefix+compression framing), not
    /// the payload schema version — those evolve independently.</summary>
    public const string CodePrefix = "STS2DOJO1.";

    private const string CodeFamilyMarker = "STS2DOJO";

    private static readonly JsonSerializerOptions FileOptions = CreateOptions(indented: true);
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(indented: false);

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        // Explicit reflection resolver: the game process configures source-gen resolvers for ITS types;
        // this mod-owned format must not depend on that (and must also work in the §5b test harness).
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        IncludeFields = true,
        WriteIndented = indented,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
            new SharedFightModelIdConverter(),
        },
    };

    public static string ToJson(SharedFightPayload payload) =>
        JsonSerializer.Serialize(payload, FileOptions);

    public static SharedFightPayload FromJson(string json)
    {
        SharedFightPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SharedFightPayload>(json, FileOptions);
        }
        catch (JsonException e)
        {
            throw new SharedFightFormatException(
                "This doesn't look like a valid exported fight (unreadable JSON).", e);
        }

        if (payload == null)
        {
            throw new SharedFightFormatException("This doesn't look like a valid exported fight (empty JSON).");
        }

        var problems = payload.GetStructuralProblems();
        if (problems.Count > 0)
        {
            throw new SharedFightFormatException(
                "This exported fight is incomplete or damaged: " + string.Join("; ", problems) + ".");
        }

        return payload;
    }

    public static string ToCode(SharedFightPayload payload)
    {
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, CompactOptions));
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
}

/// <summary>String form <c>CATEGORY.ENTRY</c>, identical to how the game's own options-level converter
/// writes ModelId into `.run`/save JSON.</summary>
internal sealed class SharedFightModelIdConverter : JsonConverter<ModelId>
{
    public override ModelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ModelId.Deserialize(reader.GetString() ?? throw new JsonException("ModelId cannot be null."));

    public override void Write(Utf8JsonWriter writer, ModelId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
