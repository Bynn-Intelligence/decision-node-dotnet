using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bynn.DecisionNode.Internal;

/// <summary>
/// Every fixed rule the API documents, checked on this machine before anything is sent,
/// in the server's order: the nesting limit, then every <c>422</c> entry of the request,
/// then the first <c>400</c> in question order, then the images, then the videos. A fault raises what the
/// server would answer: the same class, the same <c>error_type</c>, the documented message,
/// and for a <c>422</c> a <see cref="ValidationError"/> whose entries are the server's list.
/// A local error has no status and no request id; its <c>Reason</c> says what the server's
/// <c>x-decisionnode-error</c> would. A rule whose value only <c>GET /v1/models</c> states is
/// checked against the value the client holds from an earlier <c>ModelsAsync</c>, and left
/// to the server when it holds none. Token counts and upload references stay the server's.
/// </summary>
internal static class LocalChecks
{
    internal const int MaxImages = 16;
    internal const int MaxUploadFiles = 16;
    internal const int MaxVideos = 4;
    internal const double MaxFps = 4;
    internal const int MaxImageBytes = 10 * 1024 * 1024;
    internal const long MaxImagePixels = 40_000_000;
    internal const int MaxUrlChars = 2048;
    internal const int MaxChoiceOptions = 255;
    internal const int MaxScoreLevels = 10;
    internal const int MaxGridValues = 4096;
    internal const int MaxNesting = 128;
    internal const int MaxWindow = 64;
    internal const int MaxTtlSeconds = 3600;
    internal const long MaxSeq = 9007199254740991;
    internal const int MaxListLimit = 100;
    internal const int MaxCustomIdChars = 512;
    private const decimal TwoTo53 = 9007199254740992m;
    private const int MaxNumberDecimals = 6;

    internal const string Decide = "decide";
    internal const string SystemOne = "systemone";

    private static readonly string[] DecideTypes = { "truth", "noul", "choice", "score", "number", "point", "box" };
    private static readonly string[] Units = { "pixels", "normalized" };
    private static readonly string[] SystemOneTypes = { "noul", "choice", "score" };
    private static readonly string[] MediaTypes = { ImageHeader.Jpeg, ImageHeader.Png, ImageHeader.WebP };

    // ------------------------------------------------------------------ requests

    /// <summary>
    /// A decide body (<c>model</c>, <c>state</c>, <c>questions</c>) with the images of
    /// <paramref name="imageIds"/> beside it, on <paramref name="profile"/>. The ids are what a
    /// point or box question may name; <c>null</c> where the request carries no images of its
    /// own (a session's open, whose images come with its frames), so those rules are skipped.
    /// </summary>
    /// <remarks>
    /// <paramref name="entriesAfter"/> are listed after the state's and the questions' entries
    /// (the image and video counts), so one <see cref="ValidationError"/> holds them all.
    /// </remarks>
    internal static void Request(JsonObject body, IReadOnlyList<string>? imageIds, string profile, JsonObject? held, List<JsonNode?>? entriesBefore = null, IEnumerable<JsonNode?>? entriesAfter = null)
    {
        // The images sit at body.images[i], three levels deep, whatever their form.
        Nesting(Math.Max(Depth(body), imageIds is { Count: > 0 } ? 3 : 0));
        var entries = entriesBefore ?? new List<JsonNode?>();
        var deferred = new List<DecisionNodeError>();
        if (body.TryGetPropertyValue("state", out var state) && state is not null && !IsStringObjectOrArray(state))
        {
            entries.AddRange(SdlEntries(new JsonArray("body", "state"), state));
        }
        if (body["questions"] is JsonObject questions)
        {
            foreach (var (id, q) in questions)
            {
                Question(id, q, entries, deferred, profile, held, imageIds);
            }
        }
        if (entriesAfter is not null)
        {
            entries.AddRange(entriesAfter);
        }
        if (entries.Count > 0)
        {
            throw Unprocessable(entries);
        }
        if (deferred.Count > 0)
        {
            throw deferred[0];
        }
    }

    private static void Question(string id, JsonNode? node, List<JsonNode?> entries, List<DecisionNodeError> deferred, string profile, JsonObject? held, IReadOnlyList<string>? imageIds)
    {
        var at = new JsonArray("body", "questions", id);
        if (node is not JsonObject q)
        {
            entries.Add(Entry("model_type", at, "Input should be a valid dictionary or object", node));
            return;
        }
        if (q["type"] is null)
        {
            entries.Add(Entry("missing", Loc(at, "type"), "Field required", q));
            return;
        }
        var type = JsonValues.AsString(q["type"]);
        if (type is null || Array.IndexOf(profile == SystemOne ? SystemOneTypes : DecideTypes, type) < 0)
        {
            deferred.Add(BadRequest("Invalid request.", "api_usage_error", $"question '{id}': unknown type {q["type"]!.ToJsonString()}"));
            return;
        }
        var loc = Loc(at, type);
        var instructions = q["instructions"];
        if (instructions is not null && !IsStringObjectOrArray(instructions))
        {
            entries.AddRange(SdlEntries(Loc(loc, "instructions"), instructions));
        }
        var criteria = q["criteria"];
        switch (type)
        {
            case "number":
                if (JsonValues.AsLong(held?["number_max_grid_values"]) == 0)
                {
                    deferred.Add(BadRequest($"Model {JsonValues.AsString(held!["name"])} does not answer number questions.", "api_usage_error", null));
                    return;
                }
                Number(id, q, loc, entries, deferred, (int?)JsonValues.AsLong(held?["number_max_grid_values"]) ?? MaxGridValues);
                break;
            case "choice":
                var mostOptions = (int?)JsonValues.AsLong(held?["max_choice_options"]) ?? MaxChoiceOptions;
                if (criteria is null)
                {
                    entries.Add(Entry("missing", Loc(loc, "criteria"), "Field required", q));
                }
                else if (criteria is not JsonObject options)
                {
                    entries.Add(Entry("dict_type", Loc(loc, "criteria"), "Input should be a valid dictionary", criteria));
                }
                else
                {
                    foreach (var (name, description) in options)
                    {
                        if (description is not null && !IsStringObjectOrArray(description))
                        {
                            entries.AddRange(SdlEntries(Loc(loc, "criteria", name), description));
                        }
                    }
                    if (options.Count == 0)
                    {
                        deferred.Add(BadRequest($"Choice question must have at least one choice: {id}", null, null));
                    }
                    else if (options.Count > mostOptions)
                    {
                        deferred.Add(BadRequest($"Too many choices. Must have at most {mostOptions} choices.", null, null));
                    }
                }
                break;
            case "point" or "box":
                Place(id, q, type, loc, entries, deferred, imageIds);
                break;
            case "score":
                var mostLevels = (int?)JsonValues.AsLong(held?["max_score_levels"]) ?? MaxScoreLevels;
                if (criteria is null)
                {
                    entries.Add(Entry("missing", Loc(loc, "criteria"), "Field required", q));
                    return;
                }
                List<JsonNode?>? levels = null;
                if (criteria is JsonArray list)
                {
                    levels = list.ToList();
                }
                else if (criteria is JsonObject keyed && profile != SystemOne && IsIndexKeyed(keyed))
                {
                    // An object keyed "0" to "K-1" is a list on /v1/decide.
                    levels = Enumerable.Range(0, keyed.Count).Select(i => keyed[i.ToString(CultureInfo.InvariantCulture)]).ToList();
                }
                if (levels is null)
                {
                    entries.Add(Entry("list_type", Loc(loc, "criteria"), "Input should be a valid list", criteria));
                    return;
                }
                if (levels.Count == 0)
                {
                    entries.Add(Entry("too_short", Loc(loc, "criteria"), "List should have at least 1 item after validation, not 0", criteria));
                    return;
                }
                for (var i = 0; i < levels.Count; i++)
                {
                    if (!IsStringObjectOrArray(levels[i]))
                    {
                        entries.AddRange(SdlEntries(Loc(loc, "criteria", i), levels[i]));
                    }
                }
                if (levels.Count > mostLevels)
                {
                    deferred.Add(BadRequest($"Too many score levels. Must have at most {mostLevels} levels.", null, null));
                }
                break;
            default: // truth, and the same type under its other wire name
                if (criteria is not null && criteria is not JsonObject)
                {
                    entries.Add(Entry("dict_type", Loc(loc, "criteria"), "Input should be a valid dictionary", criteria));
                    return;
                }
                var sides = criteria as JsonObject;
                foreach (var side in new[] { "true", "false" })
                {
                    if (sides?[side] is { } value && !IsStringObjectOrArray(value))
                    {
                        entries.AddRange(SdlEntries(Loc(loc, "criteria", side), value));
                    }
                }
                if (instructions is null && sides?["true"] is null && sides?["false"] is null)
                {
                    var name = type == "truth" ? "Truth" : "Noul";
                    deferred.Add(BadRequest($"{name} question must have criteria or instructions: {id}", null, null));
                }
                break;
        }
    }

    /// <summary>
    /// A point or box question: its <c>422</c> entries (the image, then the units), then
    /// missing instructions as the API's plain-string <c>400</c>.
    /// </summary>
    private static void Place(string id, JsonObject q, string type, JsonArray loc, List<JsonNode?> entries, List<DecisionNodeError> deferred, IReadOnlyList<string>? imageIds)
    {
        if (imageIds is not null)
        {
            var image = q["image"];
            if (imageIds.Count == 0)
            {
                entries.Add(Entry("image_required", loc, $"A {type} question needs an image in the request", q));
            }
            else if (image is null)
            {
                if (imageIds.Count >= 2)
                {
                    entries.Add(Entry("missing", Loc(loc, "image"), "Field required", q));
                }
            }
            else if (JsonValues.AsString(image) is not { } name)
            {
                entries.Add(Entry("string_type", Loc(loc, "image"), "Input should be a valid string", image));
            }
            else if (!imageIds.Contains(name))
            {
                entries.Add(Entry("unknown_image", Loc(loc, "image"), "Input should be the id of an image of the request", image));
            }
        }
        if (q["units"] is { } units && (JsonValues.AsString(units) is not { } unit || Array.IndexOf(Units, unit) < 0))
        {
            entries.Add(Entry("literal_error", Loc(loc, "units"), "Input should be 'pixels' or 'normalized'", units, new JsonObject { ["expected"] = "'pixels' or 'normalized'" }));
        }
        if (q["instructions"] is null)
        {
            var name = type == "point" ? "Point" : "Box";
            deferred.Add(BadRequest($"{name} question must have instructions: {id}", null, null));
        }
    }

    private static bool IsIndexKeyed(JsonObject o)
    {
        var i = 0;
        foreach (var key in o.Select(kv => kv.Key).OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal))
        {
            if (key != i.ToString(CultureInfo.InvariantCulture))
            {
                return false;
            }
            i++;
        }
        return true;
    }

    /// <summary>The number grid's <c>422</c> entries in the server's order, then its <c>400</c>.</summary>
    private static void Number(string id, JsonObject q, JsonArray loc, List<JsonNode?> entries, List<DecisionNodeError> deferred, int maxValues)
    {
        var defaults = new Dictionary<string, decimal> { ["min"] = 0, ["max"] = 255, ["step"] = 1 };
        var values = new Dictionary<string, decimal>();
        var ok = true;
        foreach (var name in new[] { "min", "max", "step" })
        {
            var sent = q[name];
            var at = Loc(loc, name);
            if (sent is null)
            {
                values[name] = defaults[name];
                continue;
            }
            if (sent is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
            {
                entries.Add(Entry("float_type", at, "Input should be a valid number", sent));
                ok = false;
                continue;
            }
            var exact = ExactDecimal(v, out var sign);
            if (name != "step" && (exact is null ? sign < 0 : exact < -TwoTo53))
            {
                entries.Add(Entry("greater_than_equal", at, "Input should be greater than or equal to -9007199254740992", sent, new JsonObject { ["ge"] = -9007199254740992L }));
                ok = false;
                continue;
            }
            if (name != "step" && (exact is null ? sign > 0 : exact > TwoTo53))
            {
                entries.Add(Entry("less_than_equal", at, "Input should be less than or equal to 9007199254740992", sent, new JsonObject { ["le"] = 9007199254740992L }));
                ok = false;
                continue;
            }
            if (name == "step" && (exact is null ? sign <= 0 : exact <= 0))
            {
                entries.Add(Entry("greater_than", at, "Input should be greater than 0", sent, new JsonObject { ["gt"] = 0 }));
                ok = false;
                continue;
            }
            if (exact is null)
            {
                // A step too large for decimal: a grid of one value, left to the server.
                return;
            }
            if (name != "max" && Places(exact.Value) > MaxNumberDecimals)
            {
                entries.Add(Entry("decimal_max_places", at, "Decimal input should have no more than 6 decimal places", sent, new JsonObject { ["decimal_places"] = MaxNumberDecimals }));
                ok = false;
                continue;
            }
            values[name] = exact.Value;
        }
        if (!ok)
        {
            return;
        }
        decimal lo = values["min"], hi = values["max"], step = values["step"];
        var d = Math.Max(Places(lo), Places(step));
        if (d > 0)
        {
            // Every value must keep 15 significant digits: the decimal bound.
            var bound = Pow10(15 - d) - (1m / Pow10(d));
            var text = Text(bound);
            foreach (var (name, value) in new[] { ("min", lo), ("max", hi) })
            {
                if (value > bound)
                {
                    entries.Add(Entry("less_than_equal", Loc(loc, name), "Input should be less than or equal to " + text, q[name], new JsonObject { ["le"] = JsonNumber(bound) }));
                    return;
                }
                if (value < -bound)
                {
                    entries.Add(Entry("greater_than_equal", Loc(loc, name), "Input should be greater than or equal to -" + text, q[name], new JsonObject { ["ge"] = JsonNumber(-bound) }));
                    return;
                }
            }
        }
        if (lo > hi)
        {
            var maxSent = q["max"]?.DeepClone() ?? JsonValue.Create(255);
            var minSent = q["min"]?.DeepClone() ?? JsonValue.Create(0);
            entries.Add(Entry("less_than_equal", Loc(loc, "min"), "Input should be less than or equal to " + maxSent.ToJsonString(), minSent, new JsonObject { ["le"] = maxSent.DeepClone() }));
            return;
        }
        var scale = Pow10(d);
        var count = ((long)decimal.Floor(hi * scale) - (long)(lo * scale)) / (long)(step * scale) + 1;
        if (count > maxValues)
        {
            entries.Add(Entry("too_long", loc, $"Grid should have at most {maxValues} values, not {count}", q, new JsonObject
            {
                ["field_type"] = "Grid",
                ["max_length"] = maxValues,
                ["actual_length"] = count,
            }));
            return;
        }
        if (q["instructions"] is null || JsonValues.AsString(q["instructions"]) == string.Empty)
        {
            deferred.Add(BadRequest($"Number question must have instructions: {id}", null, null));
        }
    }

    /// <summary>
    /// The exact decimal of a JSON number, read from its shortest form. <c>null</c> when it is
    /// beyond decimal's range; <paramref name="sign"/> then says which side.
    /// </summary>
    private static decimal? ExactDecimal(JsonValue v, out int sign)
    {
        var text = v.ToJsonString();
        sign = text.StartsWith('-') ? -1 : 1;
        if (text.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble))
        {
            if (asDouble == 0)
            {
                sign = 0;
            }
            text = asDouble.ToString("R", CultureInfo.InvariantCulture);
        }
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }
        return null;
    }

    private static int Places(decimal value)
    {
        var normal = value / 1.000000000000000000000000000000000m;
        return (decimal.GetBits(normal)[3] >> 16) & 0xFF;
    }

    private static decimal Pow10(int n)
    {
        var r = 1m;
        for (var i = 0; i < n; i++)
        {
            r *= 10;
        }
        return r;
    }

    private static string Text(decimal d) => d == 0 ? "0" : d.ToString("0.############################", CultureInfo.InvariantCulture);

    private static JsonNode JsonNumber(decimal d)
    {
        var text = Text(d);
        return text.Contains('.')
            ? JsonValue.Create(double.Parse(text, CultureInfo.InvariantCulture))
            : JsonValue.Create(long.Parse(text, CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ nesting

    /// <summary>How many objects and arrays sit inside each other, the outermost included.</summary>
    internal static int Depth(JsonNode? node)
    {
        var deepest = 0;
        var stack = new Stack<(JsonNode Node, int Depth)>();
        if (node is JsonObject or JsonArray)
        {
            stack.Push((node, 1));
        }
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            deepest = Math.Max(deepest, depth);
            IEnumerable<JsonNode?> children = current is JsonObject o ? o.Select(kv => kv.Value) : (JsonArray)current;
            foreach (var child in children)
            {
                if (child is JsonObject or JsonArray)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }
        return deepest;
    }

    private static void Nesting(int depth)
    {
        if (depth > MaxNesting)
        {
            throw BadRequest("Invalid request.", "api_usage_error", $"the request nests {depth} objects and arrays inside each other; the limit is {MaxNesting}");
        }
    }

    // ------------------------------------------------------------------ images

    /// <summary>
    /// More images or videos than a request takes: the API's <c>422</c> <c>too_long</c> entries on
    /// <c>["body", "images"]</c> and <c>["body", "videos"]</c>, images first. Each lists the
    /// request's items in their wire form, a local image as its id and media type only: nothing
    /// is encoded or uploaded to build an error.
    /// </summary>
    internal static List<JsonNode?> CountEntries(IReadOnlyList<Image> images, IReadOnlyList<JsonObject> videos, JsonObject? held)
    {
        var entries = new List<JsonNode?>();
        var mostImages = Positive(held?["max_images"]) ?? Positive((held?["images"] as JsonObject)?["max_images"]) ?? MaxImages;
        if (images.Count > mostImages)
        {
            entries.Add(TooLong("images", new JsonArray(images.Select(i => (JsonNode?)ListedImage(i)).ToArray()), mostImages));
        }
        if (videos.Count > MaxVideos)
        {
            entries.Add(TooLong("videos", new JsonArray(videos.Select(v => (JsonNode?)v.DeepClone()).ToArray()), MaxVideos));
        }
        return entries;
    }

    private static long? Positive(JsonNode? node) => JsonValues.AsLong(node) is > 0 and var n ? n : null;

    private static JsonObject TooLong(string field, JsonArray input, long most) => Entry(
        "too_long",
        new JsonArray("body", field),
        $"List should have at most {most} items after validation, not {input.Count}",
        input,
        new JsonObject { ["field_type"] = "List", ["max_length"] = most, ["actual_length"] = input.Count });

    /// <summary>An image as a <c>422</c> entry lists it: its wire form, a local image's bytes left out.</summary>
    private static JsonObject ListedImage(Image image)
    {
        if (!image.IsLocal)
        {
            return image.ToJson();
        }
        var listed = new JsonObject { ["id"] = image.Id };
        if (image.MediaType is { } type)
        {
            listed["media_type"] = type;
        }
        return listed;
    }

    /// <summary>The request's images: the ids, URLs, upload references and local bytes.</summary>
    internal static void Images(IReadOnlyList<Image> images, JsonObject? held)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            if (string.IsNullOrEmpty(image.Id))
            {
                throw BadRequest($"Image {i} needs an id.", "api_usage_error", null);
            }
            if (!seen.Add(image.Id))
            {
                throw BadRequest($"Duplicate image id: {image.Id}", "api_usage_error", null);
            }
            One(image, held);
        }
    }

    /// <summary>One image's own rules.</summary>
    internal static void One(Image image, JsonObject? held)
    {
        if (image.Url is not null)
        {
            Url(image.Id, image.Url);
            return;
        }
        if (image.UploadId is not null)
        {
            if (image.DeclaredMediaType is { } type && Array.IndexOf(MediaTypes, type) < 0)
            {
                throw BadRequest($"Image {image.Id}: media_type must be image/jpeg, image/png or image/webp.", "api_usage_error", null);
            }
            return;
        }
        if (image.Fault() is { } fault)
        {
            throw BadRequest(fault, "api_usage_error", null);
        }
        var biggest = JsonValues.AsLong(held?["max_image_bytes"]) ?? MaxImageBytes;
        if (image.Data!.Value.Length > biggest)
        {
            throw BadRequest($"Image {image.Id} is larger than 10 MB.", "api_usage_error", null);
        }
    }

    /// <summary>
    /// The URL rules that need no network: <c>https</c> (else <c>api_usage_error</c>), then
    /// <c>image_url_refused</c> for the length, user information, a port other than 443, no
    /// host, or a literal address in a blocked range. A DNS name is the server's to resolve.
    /// </summary>
    internal static void Url(string id, string url)
    {
        if (!url.StartsWith("https://", StringComparison.Ordinal))
        {
            throw BadRequest($"Image {id}: only https URLs are accepted.", "api_usage_error", null);
        }
        string? reason = null;
        var rest = url["https://".Length..];
        var end = rest.IndexOfAny(new[] { '/', '?', '#' });
        var authority = end < 0 ? rest : rest[..end];
        if (url.Length > MaxUrlChars)
        {
            reason = $"the URL is {url.Length} characters, over the limit of {MaxUrlChars}";
        }
        else if (authority.Contains('@'))
        {
            reason = "the URL carries user information";
        }
        else
        {
            var (host, port) = SplitAuthority(authority);
            if (port is not (null or 443))
            {
                reason = "the URL uses a port other than 443";
            }
            else if (host.Length == 0)
            {
                reason = "the URL has no host";
            }
            else if (IsBlockedAddress(host))
            {
                reason = "the URL resolves to a private address";
            }
        }
        if (reason is not null)
        {
            throw BadRequest($"Image '{id}': {reason}.", "image_url_refused", null);
        }
    }

    /// <summary>The host and port of an authority; a port that is not a number reads as -1.</summary>
    private static (string Host, int? Port) SplitAuthority(string authority)
    {
        string host;
        string? port = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                return (string.Empty, null);
            }
            host = authority[1..close];
            var after = authority[(close + 1)..];
            if (after.StartsWith(':'))
            {
                port = after[1..];
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            port = colon < 0 ? null : authority[(colon + 1)..];
        }
        if (port is null || port.Length == 0)
        {
            return (host, null);
        }
        return (host, int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p <= 65535 ? p : -1);
    }

    private static readonly (byte[] Network, int Bits)[] BlockedV4 = Ranges(
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15",
        "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4", "255.255.255.255/32");

    private static readonly (byte[] Network, int Bits)[] BlockedV6 = Ranges(
        "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8", "2001:db8::/32", "fd00:ec2::254/128");

    private static readonly (byte[] Network, int Bits) Nat64 = Ranges("64:ff9b::/96")[0];

    private static (byte[], int)[] Ranges(params string[] cidrs) => cidrs.Select(c =>
    {
        var slash = c.IndexOf('/');
        return (IPAddress.Parse(c[..slash]).GetAddressBytes(), int.Parse(c[(slash + 1)..], CultureInfo.InvariantCulture));
    }).ToArray();

    /// <summary>Whether a host is a literal IP address in a blocked range (IPv4 inside IPv6 judged by the IPv4 address).</summary>
    internal static bool IsBlockedAddress(string host)
    {
        // Only literal addresses: a dotted quad or an IPv6 address. A name is the server's.
        var literal = host.Contains(':') || System.Text.RegularExpressions.Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}$");
        if (!literal || !IPAddress.TryParse(host, out var address))
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            else if (InRange(bytes, Nat64))
            {
                address = new IPAddress(bytes[12..]);
            }
        }
        var raw = address.GetAddressBytes();
        var ranges = address.AddressFamily == AddressFamily.InterNetwork ? BlockedV4 : BlockedV6;
        return ranges.Any(r => InRange(raw, r));
    }

    private static bool InRange(byte[] address, (byte[] Network, int Bits) range)
    {
        if (address.Length != range.Network.Length)
        {
            return false;
        }
        for (var bit = 0; bit < range.Bits; bit++)
        {
            var mask = 0x80 >> (bit % 8);
            if ((address[bit / 8] & mask) != (range.Network[bit / 8] & mask))
            {
                return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ videos

    /// <summary>
    /// The request's videos in their wire form, after the images (their count is one of the
    /// <see cref="CountEntries"/>): each video in turn (its id, unique across images and videos; its frames, each frame's time and
    /// media type; <c>fps</c>, <c>start</c> and <c>end</c>). The frame counts, the frames'
    /// bytes and sizes and the token budget are the server's.
    /// </summary>
    internal static void Videos(IReadOnlyList<JsonObject> videos, IEnumerable<string> imageIds)
    {
        if (videos.Count == 0)
        {
            return;
        }
        var seen = new HashSet<string>(imageIds, StringComparer.Ordinal);
        for (var i = 0; i < videos.Count; i++)
        {
            var video = videos[i];
            if (JsonValues.AsString(video["id"]) is not { Length: > 0 } id)
            {
                throw BadRequest($"Video {i} needs an id.", "api_usage_error", null);
            }
            if (!seen.Add(id))
            {
                throw BadRequest($"Video id '{id}' is used twice (ids are unique across images and videos).", "api_usage_error", null);
            }
            var forms = new[] { "data", "url", "frames" }.Count(name => video[name] is not null);
            if (forms > 1)
            {
                throw VideoFault(id, "needs exactly one of data, url and frames");
            }
            // A video file or a URL is never sent: this library sends videos as frames only.
            if (video["frames"] is not JsonArray frames || frames.Count == 0)
            {
                throw VideoFault(id, "frames must be a non-empty list of frames");
            }
            double? before = null;
            for (var n = 0; n < frames.Count; n++)
            {
                if (frames[n] is not JsonObject frame)
                {
                    throw VideoFault(id, $"frame {n} must be an object with t, media_type and data");
                }
                if (!IsFinite(frame["t"], out var t) || t < 0)
                {
                    throw VideoFault(id, $"frame {n}: t must be a number of seconds, at least 0");
                }
                if (before is { } previous && t <= previous)
                {
                    throw VideoFault(id, $"frame {n}: t must be larger than the frame before it ({Printf(previous)})");
                }
                before = t;
                if (JsonValues.AsString(frame["data"]) is null)
                {
                    throw VideoFault(id, $"frame {n}: data must be base64 text");
                }
                if (JsonValues.AsString(frame["media_type"]) is not { } type || Array.IndexOf(MediaTypes, type) < 0)
                {
                    throw VideoFault(id, $"frame {n}: media_type must be one of {string.Join(", ", MediaTypes)}");
                }
            }
            if (video["fps"] is { } fpsNode && (!IsFinite(fpsNode, out var fps) || fps <= 0 || fps > MaxFps))
            {
                throw VideoFault(id, $"fps must be a number above 0 and at most {Printf(MaxFps)}");
            }
            foreach (var name in new[] { "start", "end" })
            {
                if (video[name] is { } node && (!IsFinite(node, out var value) || value < 0))
                {
                    throw VideoFault(id, $"{name} must be a number of seconds, at least 0");
                }
            }
            if (video["end"] is { } endNode && IsFinite(endNode, out var end))
            {
                var start = IsFinite(video["start"], out var s) ? s : 0;
                if (end <= start)
                {
                    throw VideoFault(id, "end must be after start");
                }
            }
        }
    }

    private static BadRequestError VideoFault(string id, string problem) =>
        BadRequest($"Video '{id}': {problem}.", "api_usage_error", null);

    /// <summary>A JSON number (never a boolean) that is finite.</summary>
    private static bool IsFinite(JsonNode? node, out double value) =>
        JsonValues.TryDouble(node, out value) && double.IsFinite(value);

    /// <summary>
    /// A number as the API writes it in a message, as printf's <c>%g</c> does: six significant
    /// digits without trailing zeros (<c>1.5</c>, <c>4</c>, <c>0.333333</c>), and the exponent
    /// form below 0.0001 or from a million (<c>1e+06</c>).
    /// </summary>
    internal static string Printf(double value)
    {
        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }
        // The value rounded to six significant digits gives the exponent that picks the form.
        var scientific = value.ToString("E5", CultureInfo.InvariantCulture);
        var e = scientific.IndexOf('E');
        var exponent = int.Parse(scientific[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (exponent is >= -4 and < 6)
        {
            return TrimZeros(value.ToString("F" + (5 - exponent).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        }
        var sign = exponent < 0 ? "-" : "+";
        return TrimZeros(scientific[..e]) + "e" + sign + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
    }

    private static string TrimZeros(string text) => text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;

    // ------------------------------------------------------------------ sessions

    /// <summary>
    /// An open body: nesting, <c>instructions</c> and <c>state</c> of the three types,
    /// <c>window</c> 1 to 64 and <c>ttl_seconds</c> 1 to 3,600 (held values replace the
    /// bounds), every question rule, and a model the client knows serves no sessions.
    /// </summary>
    internal static void SessionOpen(JsonObject body, JsonObject? held)
    {
        Nesting(Depth(body));
        var sessions = held?["sessions"] as JsonObject;
        var entries = new List<JsonNode?>();
        foreach (var name in new[] { "instructions", "state" })
        {
            if (body[name] is { } value && !IsStringObjectOrArray(value))
            {
                entries.AddRange(SdlEntries(new JsonArray("body", name), value));
            }
        }
        var bounds = new[]
        {
            ("window", JsonValues.AsLong(sessions?["max_window"]) ?? MaxWindow),
            ("ttl_seconds", JsonValues.AsLong(sessions?["max_ttl_seconds"]) ?? MaxTtlSeconds),
        };
        foreach (var (name, high) in bounds)
        {
            if (body[name] is not { } sent)
            {
                continue;
            }
            var value = JsonValues.AsLong(sent);
            if (value is null)
            {
                entries.Add(Entry("int_type", new JsonArray("body", name), "Input should be a valid integer", sent));
            }
            else if (value < 1)
            {
                entries.Add(Entry("greater_than_equal", new JsonArray("body", name), "Input should be greater than or equal to 1", sent, new JsonObject { ["ge"] = 1 }));
            }
            else if (value > high)
            {
                entries.Add(Entry("less_than_equal", new JsonArray("body", name), $"Input should be less than or equal to {high}", sent, new JsonObject { ["le"] = high }));
            }
        }
        Request(new JsonObject { ["questions"] = body["questions"]?.DeepClone() }, null, Decide, held, entries);
        if (sessions?["enabled"] is JsonValue enabled && enabled.GetValueKind() == JsonValueKind.False)
        {
            throw BadRequest($"Model {JsonValues.AsString(held!["name"])} does not serve sessions.", "api_usage_error", null);
        }
    }

    /// <summary>A frame's fixed rules, as the server's frame error would name them (<c>api_usage_error</c>).</summary>
    internal static void Frame(long? seq, JsonNode? frame, bool hasImage, IReadOnlyList<string>? questions, IReadOnlyList<string> sessionQuestions)
    {
        if (seq is < 0 or > MaxSeq)
        {
            throw BadRequest($"seq: must be an integer from 0 to {MaxSeq}.", "api_usage_error", null);
        }
        if (frame is null && !hasImage)
        {
            throw BadRequest("frame: a frame needs a frame value or an image.", "api_usage_error", null);
        }
        if (frame is not null && (!IsStringObjectOrArray(frame) || JsonValues.AsString(frame) == string.Empty))
        {
            throw BadRequest("frame: must be a string, an object or an array, not empty.", "api_usage_error", null);
        }
        if (questions is not null)
        {
            if (questions.Count == 0)
            {
                throw BadRequest("questions: must be a non-empty list of the session's question ids.", "api_usage_error", null);
            }
            if (sessionQuestions.Count > 0)
            {
                foreach (var id in questions)
                {
                    if (!sessionQuestions.Contains(id))
                    {
                        throw BadRequest($"questions: unknown question id '{id}'", "api_usage_error", null);
                    }
                }
            }
        }
        if (frame is not null && Depth(frame) + 1 > MaxNesting)
        {
            throw BadRequest($"The frame nests more than {MaxNesting} objects and arrays inside each other.", "api_usage_error", null);
        }
    }

    // ------------------------------------------------------------------ batches

    /// <summary>
    /// The <c>custom_id</c> rules of one create or add call (lines numbered from 1 within
    /// the call), an empty add, and the held <c>requests_per_batch</c> and <c>max_custom_id_chars</c>.
    /// </summary>
    internal static void BatchLines(IReadOnlyList<string?> customIds, bool allowEmpty, JsonObject? held)
    {
        if (customIds.Count == 0 && !allowEmpty)
        {
            throw BadRequest("The call holds no requests.", "api_usage_error", null);
        }
        var batch = held?["batch"] as JsonObject;
        if (JsonValues.AsLong(batch?["requests_per_batch"]) is { } most && customIds.Count > most)
        {
            throw BadRequest($"The batch would hold {customIds.Count} requests. The limit is {most}.", "api_usage_error", null);
        }
        var longest = JsonValues.AsLong(batch?["max_custom_id_chars"]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < customIds.Count; i++)
        {
            var n = i + 1;
            var id = customIds[i];
            if (id is null)
            {
                throw BadRequest($"Line {n} has no custom_id.", "api_usage_error", null);
            }
            if (id.Length < 1 || id.Length > (longest ?? MaxCustomIdChars))
            {
                throw BadRequest($"Line {n}: custom_id must be a string of 1 to {longest ?? MaxCustomIdChars} characters.", "api_usage_error", null);
            }
            if (!seen.Add(id))
            {
                var shown = JsonValue.Create(id).ToJsonString(JsonValues.Options);
                throw BadRequest($"Line {n}: custom_id {(shown.Length <= 64 ? shown : shown[..64] + "...")} is already in the batch.", "api_usage_error", null);
            }
        }
    }

    /// <summary><c>offset</c> 0 or more, <c>limit</c> 1 to 100.</summary>
    internal static void ListPage(int? offset, int? limit)
    {
        if (offset is < 0)
        {
            throw BadRequest($"Query parameter offset must be an integer of 0 or more, got {offset}.", "api_usage_error", null);
        }
        if (limit is < 1 or > MaxListLimit)
        {
            throw BadRequest($"Query parameter limit must be an integer from 1 to {MaxListLimit}, got {limit}.", "api_usage_error", null);
        }
    }

    // ------------------------------------------------------------------ errors and entries

    private static bool IsStringObjectOrArray(JsonNode? node) =>
        node is JsonObject or JsonArray || (node is JsonValue v && v.GetValueKind() == JsonValueKind.String);

    private static IEnumerable<JsonNode?> SdlEntries(JsonArray loc, JsonNode? value) => new[]
    {
        Entry("string_type", Loc(loc, "str"), "Input should be a valid string", value),
        Entry("dict_type", Loc(loc, "dict[any,any]"), "Input should be a valid dictionary", value),
        Entry("list_type", Loc(loc, "list[any]"), "Input should be a valid list", value),
    };

    private static JsonArray Loc(JsonArray loc, params object[] more)
    {
        var copy = (JsonArray)loc.DeepClone();
        foreach (var part in more)
        {
            copy.Add(part is int i ? JsonValue.Create(i) : JsonValue.Create((string)part));
        }
        return copy;
    }

    private static JsonObject Entry(string type, JsonArray loc, string msg, JsonNode? input, JsonObject? ctx = null)
    {
        var e = new JsonObject
        {
            ["type"] = type,
            ["loc"] = loc.DeepClone(),
            ["msg"] = msg,
            ["input"] = input?.DeepClone(),
        };
        if (ctx is not null)
        {
            e["ctx"] = ctx;
        }
        return e;
    }

    /// <summary>The <see cref="BadRequestError"/> the API answers a fault with, raised before any request.</summary>
    internal static BadRequestError BadRequest(string message, string? errorType, string? reason = null) =>
        new(new ErrorDetails { Message = message, ErrorType = errorType, Reason = reason });

    /// <summary>The API's <c>422</c>, raised before any request: the entries the server would list.</summary>
    private static ValidationError Unprocessable(List<JsonNode?> entries)
    {
        var first = entries[0] as JsonObject;
        var reason = first is null ? null : $"{string.Join(".", ((JsonArray)first["loc"]!).Skip(1).Select(p => p!.ToString()))}: {first["msg"]}"
            + (entries.Count > 1 ? $" and {entries.Count - 1} more" : string.Empty);
        return new ValidationError(new ErrorDetails { Message = string.Empty, Reason = reason }, new JsonArray(entries.ToArray()));
    }
}
