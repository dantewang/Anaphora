using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anaphora.Core;

/// <summary>
/// A 64-bit hash as "0x677373E3E34F874F". As a JSON number it would be twenty
/// decimal digits nobody can compare against the probe's output by eye, and
/// beyond what some JSON readers hold exactly. Plain numbers are still accepted.
/// </summary>
internal sealed class HexUInt64JsonConverter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetUInt64();
        }

        string text = (reader.GetString() ?? string.Empty).Trim();
        ReadOnlySpan<char> digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text;

        return ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value)
            ? value
            : throw new JsonException($"'{text}' is not a hex hash like 0x677373E3E34F874F.");
    }

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
        writer.WriteStringValue(string.Create(CultureInfo.InvariantCulture, $"0x{value:X16}"));
}
