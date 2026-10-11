using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// A booster: a specialist model that reads the request's images before the decision. Its
/// reading is added to what the model sees as evidence and comes back in
/// <see cref="Decision.Evidence"/>. A booster never answers a question of its own.
/// </summary>
/// <remarks>
/// A name converts implicitly (<c>"age"</c> is sent as <c>"age"</c>); <see cref="Of"/> builds
/// the object form, <c>{"name", "images"?, "require"?, "options"?}</c>, with each argument
/// left out when not given, and with no argument but the name it is the name alone; <see cref="FromJson(JsonNode)"/> takes the wire form itself. Names
/// are not a closed list, so a booster added on the server works with this version, and the
/// library checks nothing about boosters before sending: the API answers an unknown name, a
/// booster turned off for the workspace or a malformed list before any booster runs and before
/// any charge.
/// </remarks>
/// <example>
/// <code>
/// var d = await dn.DecideAsync(new DecideRequest
/// {
///     Images = new[] { Image.FromUrl("a1b2", "https://files.example.com/a1b2.jpg") },
///     Questions = new Dictionary&lt;string, Question&gt; { ["adult"] = Question.Truth("Is the person an adult?") },
///     Boosters = new Booster[] { "age" },
/// });
/// var age = (AgeReading)d.Evidence!["age"].Results[0];
/// </code>
/// </example>
public sealed class Booster
{
    /// <summary>Reads a picture of a person: how old the person is, with a range and a confidence.</summary>
    public const string Age = "age";

    /// <summary>Reads any picture: how likely it was made by an image generator, and its Content Credentials.</summary>
    public const string AiGenerated = "ai_generated";

    /// <summary>Reads any picture: how many faces it shows and where.</summary>
    public const string FaceDetection = "face_detection";

    /// <summary>
    /// Reads a picture of a person: whether a face matches an entry of a wanted-persons list. Off
    /// for a workspace until it is enabled for it; takes the option <c>threshold</c>.
    /// </summary>
    public const string WantedPerson = "wanted_person";

    /// <summary>
    /// Reads any picture: the text it shows, or what the option <c>prompt</c> (at most 500
    /// characters) asks it to read.
    /// </summary>
    public const string Ocr = "ocr";

    private readonly JsonNode _wire;

    private Booster(JsonNode wire)
    {
        _wire = wire;
    }

    /// <summary>The booster's name: the name itself, or the <c>name</c> field of the object form.</summary>
    public string? Name => _wire is JsonObject o ? JsonValues.AsString(o["name"]) : JsonValues.AsString(_wire);

    /// <summary>A booster by its name alone, sent as the name.</summary>
    public static Booster Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new Booster(JsonValue.Create(name));
    }

    /// <summary>
    /// A booster in its object form, or the name alone when no other argument is given, so a
    /// list of boosters without settings goes out as a plain list of names.
    /// </summary>
    /// <param name="name">The booster's name, for example <c>age</c> or <c>ai_generated</c>.</param>
    /// <param name="images">The ids of the request's images it reads. Left out when <c>null</c>: it reads every image of the request.</param>
    /// <param name="require">
    /// <c>true</c>: a booster that fails or does not answer in time fails the request with a
    /// <see cref="OverloadedError"/> (<c>booster_unavailable</c>). Left out when <c>null</c>:
    /// the decision runs without the reading, and the evidence says why.
    /// </param>
    /// <param name="options">The booster's settings, any JSON-serialisable value. Left out when <c>null</c>.</param>
    public static Booster Of(string name, IEnumerable<string>? images = null, bool? require = null, object? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (images is null && require is null && options is null)
        {
            return Named(name);
        }
        var wire = new JsonObject { ["name"] = name };
        if (images is not null)
        {
            var list = new JsonArray();
            foreach (var id in images)
            {
                ArgumentNullException.ThrowIfNull(id, nameof(images));
                list.Add(id);
            }
            wire["images"] = list;
        }
        if (require is { } r)
        {
            wire["require"] = r;
        }
        if (options is not null)
        {
            wire["options"] = JsonValues.ToNode(options, "the booster's options");
        }
        return new Booster(wire);
    }

    /// <summary>A booster in its wire form, a name or an object, sent exactly as given (copied).</summary>
    public static Booster FromJson(JsonNode wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return new Booster(wire.DeepClone());
    }

    /// <summary>A booster from any JSON-serialisable value: a name or an object in the wire form.</summary>
    public static Booster FromObject(object wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire is Booster booster)
        {
            return booster;
        }
        return JsonValues.ToNode(wire, "the booster") is { } node
            ? new Booster(node)
            : throw new ArgumentException("a booster must serialise to a name or an object", nameof(wire));
    }

    /// <summary>A booster by its name, sent as the name.</summary>
    public static implicit operator Booster(string name) => Named(name);

    /// <summary>A booster in its object wire form.</summary>
    public static implicit operator Booster(JsonObject wire) => FromJson(wire);

    /// <summary>A copy of the wire form.</summary>
    public JsonNode ToJson() => _wire.DeepClone();

    public override string ToString() => _wire.ToJsonString(JsonValues.Options);

    /// <summary>The wire form itself, for the request body. Never changed.</summary>
    internal JsonNode Wire => _wire;

    /// <summary>The boosters' wire forms in the caller's order; empty when none are given.</summary>
    internal static List<JsonNode> List(IEnumerable<Booster>? boosters, string argument)
    {
        var list = new List<JsonNode>();
        if (boosters is not null)
        {
            foreach (var booster in boosters)
            {
                ArgumentNullException.ThrowIfNull(booster, argument);
                list.Add(booster.Wire);
            }
        }
        return list;
    }

    /// <summary>
    /// Adds <c>boosters</c> to a body when there are any; left out otherwise, so a request
    /// without boosters is the same body as before.
    /// </summary>
    internal static void AddTo(JsonObject body, List<JsonNode> boosters)
    {
        if (boosters.Count > 0)
        {
            body["boosters"] = new JsonArray(boosters.Select(b => (JsonNode?)b.DeepClone()).ToArray());
        }
    }
}

/// <summary>
/// What one booster read for a decision: an entry of <see cref="Decision.Evidence"/>.
/// </summary>
public sealed class Evidence
{
    internal Evidence(string booster, JsonNode? raw)
    {
        Raw = raw;
        var obj = raw as JsonObject;
        Version = JsonValues.AsString(obj?["version"]);
        Status = JsonValues.AsString(obj?["status"]);
        LatencyMs = JsonValues.TryDouble(obj?["latency_ms"], out var ms) ? ms : null;
        BilledCalls = JsonValues.AsLong(obj?["billed_calls"]);
        var results = new List<Reading>();
        if (obj?["results"] is JsonArray list)
        {
            foreach (var item in list)
            {
                results.Add(Reading.Parse(booster, item));
            }
        }
        Results = results.AsReadOnly();
    }

    /// <summary>The version of the booster that read, for example <c>age-v3</c>.</summary>
    public string? Version { get; }

    /// <summary>
    /// <c>ok</c>; <c>timeout</c> (no answer within its time budget); <c>error</c>;
    /// <c>skipped</c> (none of the request's images is one it reads); or a newer value, as received.
    /// </summary>
    public string? Status { get; }

    /// <summary>How long the booster took, in milliseconds, as received.</summary>
    public double? LatencyMs { get; }

    /// <summary>
    /// The calls of this booster billed on this request: one per image read with status
    /// <c>ok</c>, none for a booster that timed out or failed; <c>null</c> when the server did
    /// not send it.
    /// </summary>
    public long? BilledCalls { get; }

    /// <summary>
    /// One entry per picture. A picture the booster did not read (it timed out or failed, or could
    /// not decode the picture) is an <see cref="UnknownReading"/> holding only its image, the
    /// <see cref="Status"/> saying why.
    /// </summary>
    public IReadOnlyList<Reading> Results { get; }

    /// <summary>The entry exactly as received.</summary>
    public JsonNode? Raw { get; }

    internal static IReadOnlyDictionary<string, Evidence>? Parse(JsonNode? node)
    {
        if (node is not JsonObject map)
        {
            return null;
        }
        var evidence = new OrderedMap<Evidence>();
        foreach (var (name, entry) in map)
        {
            evidence.Add(name, new Evidence(name, entry));
        }
        return evidence;
    }
}

/// <summary>
/// One booster's reading of one image: an <see cref="AgeReading"/>, an
/// <see cref="AiGeneratedReading"/> or, for a booster this version of the library does not know
/// or a reading with a missing or wrongly typed field, an <see cref="UnknownReading"/> holding
/// its raw fields. A reading never fails the decision. Numbers are passed through as received.
/// </summary>
public abstract class Reading
{
    internal Reading(string booster, JsonNode? raw)
    {
        Booster = booster;
        Raw = raw;
        Image = JsonValues.AsString((raw as JsonObject)?["image"]);
    }

    /// <summary>The booster that read, the name its evidence is listed under.</summary>
    public string Booster { get; }

    /// <summary>The id of the image read.</summary>
    public string? Image { get; }

    /// <summary>The reading exactly as received.</summary>
    public JsonNode? Raw { get; }

    internal static Reading Parse(string booster, JsonNode? node)
    {
        if (node is not JsonObject raw)
        {
            return new UnknownReading(booster, node);
        }
        try
        {
            return booster switch
            {
                DecisionNode.Booster.Age => AgeReading.Read(raw),
                DecisionNode.Booster.AiGenerated => AiGeneratedReading.Read(raw),
                _ => new UnknownReading(booster, raw),
            };
        }
        catch (MalformedReading)
        {
            return new UnknownReading(booster, raw);
        }
    }

    /// <summary>Raised while reading a known booster's fields only; never leaves this class.</summary>
    private protected sealed class MalformedReading : Exception
    {
    }

    private protected static string Text(JsonObject raw, string field) =>
        JsonValues.AsString(raw[field]) ?? throw new MalformedReading();

    private protected static double Number(JsonObject raw, string field) =>
        JsonValues.TryDouble(raw[field], out var d) ? d : throw new MalformedReading();

    private protected static long Whole(JsonObject raw, string field) =>
        JsonValues.AsLong(raw[field]) ?? throw new MalformedReading();

    /// <summary>A field that may be absent or <c>null</c>; present, it must be a whole number.</summary>
    private protected static long? OptionalWhole(JsonObject raw, string field) =>
        raw[field] is null ? null : Whole(raw, field);

    /// <summary>A field that may be absent or <c>null</c>; present, it must be a number.</summary>
    private protected static double? OptionalNumber(JsonObject raw, string field) =>
        raw[field] is null ? null : Number(raw, field);

    /// <summary>A field that may be absent or <c>null</c>; present, it must be a string.</summary>
    private protected static string? OptionalText(JsonObject raw, string field) =>
        raw[field] is null ? null : Text(raw, field);
}

/// <summary>
/// The <c>age</c> booster's reading of one image: how old the person in it is. The largest
/// face is read; with <see cref="Faces"/> 0 the picture shows no face and the age fields are
/// <c>null</c>.
/// </summary>
public sealed class AgeReading : Reading
{
    private AgeReading(JsonObject raw, long faces, long? ageYears, long? low, long? high, double? confidence)
        : base(DecisionNode.Booster.Age, raw)
    {
        Faces = faces;
        AgeYears = ageYears;
        Low = low;
        High = high;
        Confidence = confidence;
    }

    /// <summary>How many faces were found.</summary>
    public long Faces { get; }

    /// <summary>The age read, in years; <c>null</c> when no face was found.</summary>
    public long? AgeYears { get; }

    /// <summary>The low end of the range; <c>null</c> when no face was found.</summary>
    public long? Low { get; }

    /// <summary>The high end of the range; <c>null</c> when no face was found.</summary>
    public long? High { get; }

    /// <summary>How sure the reading is, from 0 to 1; <c>null</c> when no face was found.</summary>
    public double? Confidence { get; }

    internal static AgeReading Read(JsonObject raw)
    {
        _ = Text(raw, "image");
        var faces = Whole(raw, "faces");
        var ageYears = OptionalWhole(raw, "age_years");
        var low = OptionalWhole(raw, "low");
        var high = OptionalWhole(raw, "high");
        var confidence = OptionalNumber(raw, "confidence");
        if (faces > 0 && (ageYears is null || low is null || high is null || confidence is null))
        {
            throw new MalformedReading();
        }
        return new AgeReading(raw, faces, ageYears, low, high, confidence);
    }
}

/// <summary>
/// The <c>ai_generated</c> booster's reading of one image: how likely the picture was made by
/// an image generator, which one most likely, and whether the file carries Content Credentials
/// (C2PA) and who signed them.
/// </summary>
public sealed class AiGeneratedReading : Reading
{
    private AiGeneratedReading(JsonObject raw, double generated, double confidence, string method, TopGenerator? topGenerator, string? c2paSigner, string? c2pa)
        : base(DecisionNode.Booster.AiGenerated, raw)
    {
        Generated = generated;
        Confidence = confidence;
        Method = method;
        TopGenerator = topGenerator;
        C2paSigner = c2paSigner;
        C2pa = c2pa;
    }

    /// <summary>The probability that the picture was made by a generator, from 0 to 1.</summary>
    public double Generated { get; }

    /// <summary>How sure the reading is, from 0 to 1.</summary>
    public double Confidence { get; }

    /// <summary>
    /// <c>inference</c> (read from the pixels), <c>c2pa</c> (the file's Content Credentials say
    /// so), or a newer value, as received.
    /// </summary>
    public string Method { get; }

    /// <summary>
    /// The likeliest generator, or <c>null</c> unless the reading says the picture is likely
    /// generated, and on every <c>c2pa</c> reading.
    /// </summary>
    public TopGenerator? TopGenerator { get; }

    /// <summary>Who signed the file's Content Credentials, or <c>null</c> when it has none.</summary>
    public string? C2paSigner { get; }

    /// <summary>
    /// <c>untrusted</c> when the file's Content Credentials validate but their signer is not
    /// trusted, any other value as received, or <c>null</c> when the reading has none.
    /// </summary>
    public string? C2pa { get; }

    internal static AiGeneratedReading Read(JsonObject raw)
    {
        _ = Text(raw, "image");
        var generated = Number(raw, "generated");
        var confidence = Number(raw, "confidence");
        var method = Text(raw, "method");
        // The top_generator and c2pa_signer keys are always sent, null when unset; c2pa only
        // when the file carries Content Credentials.
        if (!raw.ContainsKey("top_generator") || !raw.ContainsKey("c2pa_signer"))
        {
            throw new MalformedReading();
        }
        var top = raw["top_generator"] switch
        {
            null => null,
            JsonObject t => new TopGenerator(Text(t, "name"), Number(t, "probability")),
            _ => throw new MalformedReading(),
        };
        return new AiGeneratedReading(
            raw, generated, confidence, method, top, OptionalText(raw, "c2pa_signer"), OptionalText(raw, "c2pa"));
    }
}

/// <summary>The likeliest image generator of an <see cref="AiGeneratedReading"/>.</summary>
/// <param name="Name">The generator's name, as received.</param>
/// <param name="Probability">The probability that it made the picture, from 0 to 1.</param>
public sealed record TopGenerator(string Name, double Probability);

/// <summary>
/// A reading of a booster this version of the library does not know, or a known booster's
/// reading with a missing or wrongly typed field: its raw fields, kept as received.
/// </summary>
public sealed class UnknownReading : Reading
{
    internal UnknownReading(string booster, JsonNode? raw)
        : base(booster, raw)
    {
    }
}

/// <summary>
/// One booster's status and time from the <c>x-decisionnode-boosters</c> response header: an
/// entry of <see cref="DecisionNodeError.Boosters"/>.
/// </summary>
/// <param name="Status"><c>ok</c>, <c>timeout</c>, <c>error</c>, <c>skipped</c> or a newer value, as sent.</param>
/// <param name="LatencyMs">The booster's time in milliseconds; <c>null</c> when the header gives none.</param>
public sealed record BoosterStatus(string Status, long? LatencyMs)
{
    private static readonly IReadOnlyDictionary<string, BoosterStatus> Empty = new OrderedMap<BoosterStatus>();

    /// <summary>
    /// Parses <c>age=ok:212,ai_generated=timeout:800</c> into booster name to status, in the
    /// header's order. An entry without <c>=</c> is skipped; a missing or non-integer time is
    /// <c>null</c>. Empty when the header is absent.
    /// </summary>
    internal static IReadOnlyDictionary<string, BoosterStatus> ParseHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return Empty;
        }
        var map = new OrderedMap<BoosterStatus>();
        foreach (var part in header.Split(','))
        {
            var entry = part.Trim();
            var eq = entry.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var name = entry[..eq].Trim();
            var rest = entry[(eq + 1)..].Trim();
            var colon = rest.LastIndexOf(':');
            var status = colon < 0 ? rest : rest[..colon].Trim();
            long? latency = colon >= 0 && long.TryParse(rest[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : null;
            map.Add(name, new BoosterStatus(status, latency));
        }
        return map;
    }
}

/// <summary>A read-only map that keeps the order its entries were added in; a repeated key keeps its first place and takes the later value.</summary>
internal sealed class OrderedMap<TValue> : IReadOnlyDictionary<string, TValue>
{
    private readonly List<string> _keys = new();
    private readonly Dictionary<string, TValue> _values = new(StringComparer.Ordinal);

    internal void Add(string key, TValue value)
    {
        if (!_values.ContainsKey(key))
        {
            _keys.Add(key);
        }
        _values[key] = value;
    }

    public TValue this[string key] => _values[key];

    public IEnumerable<string> Keys => _keys;

    public IEnumerable<TValue> Values => _keys.Select(k => _values[k]);

    public int Count => _keys.Count;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value) => _values.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => _keys.Select(k => new KeyValuePair<string, TValue>(k, _values[k])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
