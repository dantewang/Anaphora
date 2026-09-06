using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anaphora.Core;

/// <summary>Reads and writes profiles as JSON.</summary>
public static class ProfileStore
{
    /// <summary>
    /// Reflection-based on purpose. Nothing here is trimmed or AOT-compiled, a
    /// profile is deserialised once at startup, and the source generator's
    /// interaction with the polymorphic ROI hierarchy plus a custom struct
    /// converter is a source of surprises this does not need.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new RgbJsonConverter(),
        },
    };

    public static string Serialize(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(profile, Options);
    }

    public static GameProfile Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<GameProfile>(json, Options)
            ?? throw new JsonException("profile JSON deserialised to null.");
    }

    public static GameProfile Load(string path) => Deserialize(File.ReadAllText(path));

    public static void Save(string path, GameProfile profile) =>
        File.WriteAllText(path, Serialize(profile));
}

/// <summary>Keeps colours as "#RRGGBB" instead of three numeric fields.</summary>
internal sealed class RgbJsonConverter : JsonConverter<Rgb>
{
    public override Rgb Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Rgb.Parse(reader.GetString() ?? throw new JsonException("expected a #RRGGBB colour string."));

    public override void Write(Utf8JsonWriter writer, Rgb value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
