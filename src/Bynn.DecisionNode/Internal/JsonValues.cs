using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bynn.DecisionNode.Internal;

/// <summary>Turns caller values into JSON nodes, and nodes into request bytes.</summary>
internal static class JsonValues
{
    /// <summary>
    /// Serializer settings for caller data. Property names are kept exactly as written,
    /// and non-ASCII text is written as UTF-8 instead of \u escapes.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Converts any JSON-serialisable value to a detached <see cref="JsonNode"/>.
    /// A <see cref="JsonNode"/> is copied, a <see cref="JsonElement"/> is read as is,
    /// and everything else goes through System.Text.Json.
    /// </summary>
    internal static JsonNode? ToNode(object? value, string what)
    {
        try
        {
            switch (value)
            {
                case null:
                    return null;
                case JsonNode node:
                    return node.DeepClone();
                case JsonElement element:
                    return element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());
                case JsonDocument document:
                    return JsonNode.Parse(document.RootElement.GetRawText());
                case string text:
                    return JsonValue.Create(text);
                default:
                    return JsonSerializer.SerializeToNode(value, value.GetType(), Options);
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            throw new ArgumentException($"{what} cannot be serialised to JSON: {ex.Message}", ex);
        }
    }

    internal static byte[] ToUtf8(JsonNode? node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            if (node is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                node.WriteTo(writer, Options);
            }
        }
        return buffer.ToArray();
    }

    internal static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    internal static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v)
        {
            return false;
        }
        if (v.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }
        if (v.TryGetValue(out double d))
        {
            value = d;
            return true;
        }
        if (v.TryGetValue(out JsonElement e) && e.TryGetDouble(out d))
        {
            value = d;
            return true;
        }
        // A node built in code from an integer or decimal.
        try
        {
            value = JsonSerializer.Deserialize<double>(v.ToJsonString());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static long? AsLong(JsonNode? node)
    {
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
        {
            if (v.TryGetValue(out long l))
            {
                return l;
            }
            if (v.TryGetValue(out JsonElement e) && e.TryGetInt64(out l))
            {
                return l;
            }
            if (TryDouble(node, out var d) && d == Math.Floor(d) && Math.Abs(d) < 9.0e15)
            {
                return (long)d;
            }
        }
        return null;
    }
}
