using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace Bynn.DecisionNode.Conformance;

/// <summary>The suite's comparison rules: numbers as exact doubles, objects by key, partial expectations.</summary>
public static class Compare
{
    /// <summary>Keys whose values compare whole, even inside a partial expectation.</summary>
    private static readonly HashSet<string> Exact = new() { "probabilities", "legend", "raw", "entries", "safety", "body", "limits", "evidence", "boosters" };

    /// <summary>
    /// Keys compared whole and in their key order too (suite 1.11): a decision's evidence is
    /// in the request's order, an error's boosters in the header's.
    /// </summary>
    private static readonly HashSet<string> Ordered = new() { "evidence", "boosters" };

    /// <summary>Reading options deep enough for the nesting cases (the API's limit is 128).</summary>
    public static readonly JsonDocumentOptions Deep = new() { MaxDepth = 1024 };

    /// <summary>Re-reads a node from its JSON text, so every value is backed the same way.</summary>
    public static JsonNode? Canon(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString(), documentOptions: Deep);

    public static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        switch (a)
        {
            case JsonObject oa when b is JsonObject ob:
                if (oa.Count != ob.Count)
                {
                    return false;
                }
                foreach (var (k, v) in oa)
                {
                    if (!ob.TryGetPropertyValue(k, out var w) || !Same(v, w))
                    {
                        return false;
                    }
                }
                return true;
            case JsonArray la when b is JsonArray lb:
                return la.Count == lb.Count && la.Zip(lb).All(p => Same(p.First, p.Second));
            case JsonValue va when b is JsonValue vb:
                var ka = va.GetValueKind();
                var kb = vb.GetValueKind();
                if (ka == JsonValueKind.Number && kb == JsonValueKind.Number)
                {
                    return Num(va) == Num(vb);
                }
                if (ka != kb)
                {
                    return false;
                }
                return ka switch
                {
                    JsonValueKind.String => string.Equals(va.GetValue<string>(), vb.GetValue<string>(), StringComparison.Ordinal),
                    _ => true, // true, false, null: the kind is the value
                };
            default:
                return false;
        }
    }

    private static double Num(JsonValue v) =>
        v.TryGetValue(out JsonElement e) ? e.GetDouble() : JsonSerializer.Deserialize<double>(v.ToJsonString());

    /// <summary>Every key of <paramref name="expected"/> must be in <paramref name="actual"/> with the same value.</summary>
    public static void Match(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is JsonObject eo && actual is JsonObject ao)
        {
            foreach (var (k, v) in eo)
            {
                if (!ao.TryGetPropertyValue(k, out var got))
                {
                    throw Fail($"{path}.{k}: missing (expected {Short(v)})");
                }
                if (Exact.Contains(k))
                {
                    if (!Same(v, got))
                    {
                        throw Fail($"{path}.{k}: expected {Short(v)}, got {Short(got)}");
                    }
                    if (Ordered.Contains(k) && v is JsonObject ov && got is JsonObject og && !ov.Select(p => p.Key).SequenceEqual(og.Select(p => p.Key)))
                    {
                        throw Fail($"{path}.{k}: expected the order {string.Join(", ", ov.Select(p => p.Key))}, got {string.Join(", ", og.Select(p => p.Key))}");
                    }
                }
                else
                {
                    Match(v, got, $"{path}.{k}");
                }
            }
            return;
        }
        if (!Same(expected, actual))
        {
            throw Fail($"{path}: expected {Short(expected)}, got {Short(actual)}");
        }
    }

    public static string Short(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length < 200 ? text : text[..197] + "...";
    }

    public static XunitException Fail(string message) => new(message);

    public static (bool Found, JsonNode? Value) Pointer(JsonNode? doc, string pointer)
    {
        if (pointer.Length == 0)
        {
            return (true, doc);
        }
        var cur = doc;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var part = raw.Replace("~1", "/").Replace("~0", "~");
            if (cur is JsonObject o && o.TryGetPropertyValue(part, out var next))
            {
                cur = next;
            }
            else if (cur is JsonArray a && int.TryParse(part, out var i) && i >= 0 && i < a.Count)
            {
                cur = a[i];
            }
            else
            {
                return (false, null);
            }
        }
        return (true, cur);
    }

    public static bool IsType(JsonNode? v, string type) => type switch
    {
        "string" => v is JsonValue s && s.GetValueKind() == JsonValueKind.String,
        "number" => v is JsonValue n && n.GetValueKind() == JsonValueKind.Number,
        "integer" => v is JsonValue i && i.GetValueKind() == JsonValueKind.Number && Num(i) == Math.Floor(Num(i)),
        "boolean" => v is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "null" => v is null,
        "array" => v is JsonArray,
        "object" => v is JsonObject,
        _ => throw new ArgumentException("unknown type " + type),
    };

    /// <summary>The keys of the object at <paramref name="pointer"/>, in the order they appear in the text.</summary>
    public static List<string> KeyOrder(string json, string pointer)
    {
        using var doc = JsonDocument.Parse(json);
        var cur = doc.RootElement;
        if (pointer.Length > 0)
        {
            foreach (var raw in pointer[1..].Split('/'))
            {
                var part = raw.Replace("~1", "/").Replace("~0", "~");
                if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(part, out var next))
                {
                    cur = next;
                }
                else
                {
                    return new List<string>();
                }
            }
        }
        return cur.ValueKind == JsonValueKind.Object ? cur.EnumerateObject().Select(p => p.Name).ToList() : new List<string>();
    }
}
