using System.Security.Cryptography;
using System.Text.Json;

namespace AzureArchive.VideoTools.Formats.Aap;

internal static class CanonicalJsonFingerprint
{
    public static string Compute(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, element);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element
                             .EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                WriteNumber(writer, element);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;

            default:
                throw new JsonException($"Unsupported JSON value kind {element.ValueKind}.");
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetInt64(out long signed))
        {
            writer.WriteNumberValue(signed);
            return;
        }

        if (element.TryGetUInt64(out ulong unsigned))
        {
            writer.WriteNumberValue(unsigned);
            return;
        }

        if (element.TryGetDecimal(out decimal decimalValue))
        {
            writer.WriteNumberValue(decimalValue);
            return;
        }

        writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
    }
}
