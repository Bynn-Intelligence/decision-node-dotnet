using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>The result of one <c>POST /v1/decide</c>.</summary>
public sealed class Decision
{
    internal Decision(JsonObject body, Func<string, string?> header, int status = 200)
    {
        Raw = body;
        Status = status;
        Header = header;
        Model = JsonValues.AsString(body["model"]);
        var answers = new Dictionary<string, Answer>(StringComparer.Ordinal);
        if (body["answers"] is JsonObject map)
        {
            foreach (var (id, node) in map)
            {
                answers[id] = Answer.Parse(id, node);
            }
        }
        else if (body["answers"] is not null)
        {
            throw new DecisionNodeError("the response field 'answers' is not a JSON object");
        }
        Answers = new ReadOnlyDictionary<string, Answer>(answers);
        Usage = Session.UsageOf(body["usage"]) ?? new Usage(null, null);
        var warnings = new List<string>();
        if (body["warnings"] is JsonArray sent)
        {
            foreach (var warning in sent)
            {
                if (JsonValues.AsString(warning) is { } text)
                {
                    warnings.Add(text);
                }
            }
        }
        Warnings = warnings.AsReadOnly();
        RequestId = header("x-request-id");
        LatencyMs = double.TryParse(header("x-decisionnode-latency-ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : null;
        Safety = Safety.Parse(header("x-decisionnode-safety"));
        Evidence = Bynn.DecisionNode.Evidence.Parse(body["evidence"]);
        Fallback = header("x-decisionnode-fallback") is { } served
            ? new Fallback(served, header("x-decisionnode-requested-model"))
            : null;
    }

    /// <summary>The answers, by question id.</summary>
    public IReadOnlyDictionary<string, Answer> Answers { get; }

    /// <summary>The HTTP status of the response.</summary>
    internal int Status { get; }

    /// <summary>Reads a response header by name.</summary>
    internal Func<string, string?> Header { get; }

    /// <summary>The answer to the question with this id.</summary>
    /// <exception cref="KeyNotFoundException">No answer has this id.</exception>
    public Answer this[string id] => Answers[id];

    /// <summary>
    /// The pinned id of the model version that answered, <c>decisionnode-&lt;family&gt;-&lt;version&gt;</c>
    /// (for a fine-tuned model its pinned name, for example <c>decisionnode-flash@acme/fraud:3</c>).
    /// </summary>
    public string? Model { get; }

    /// <summary>The tokens the request used, and for a request with videos the frames and seconds read.</summary>
    public Usage Usage { get; }

    /// <summary>
    /// The server's notes on the request exactly as sent, for example that it read a video at
    /// a lower rate or resolution than asked. Empty when the response has none.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The <c>x-request-id</c> response header.</summary>
    public string? RequestId { get; }

    /// <summary>The server's processing time in milliseconds, from the <c>x-decisionnode-latency-ms</c> header.</summary>
    public double? LatencyMs { get; }

    /// <summary>The safety check's result, or <c>null</c> when the server sent none (a self-hosted server).</summary>
    public Safety? Safety { get; }

    /// <summary>
    /// What each booster the request named read, by booster name in the request's order;
    /// <c>null</c> when the request named none.
    /// </summary>
    public IReadOnlyDictionary<string, Evidence>? Evidence { get; }

    /// <summary>
    /// <c>null</c> on an ordinary answer. When the model asked for had no ready worker and
    /// another model answered instead, the model that answered and the one asked for, from the
    /// <c>x-decisionnode-fallback</c> and <c>x-decisionnode-requested-model</c> headers.
    /// <see cref="Model"/> is the model that answered either way.
    /// </summary>
    public Fallback? Fallback { get; }

    /// <summary>The parsed response body, including any field this version of the library does not know.</summary>
    public JsonObject Raw { get; }

    /// <summary>The answer with this id as a <see cref="TruthAnswer"/>.</summary>
    public TruthAnswer Truth(string id) => this[id].AsTruth();

    /// <summary>The answer with this id as a <see cref="ChoiceAnswer"/>.</summary>
    public ChoiceAnswer Choice(string id) => this[id].AsChoice();

    /// <summary>The answer with this id as a <see cref="ScoreAnswer"/>.</summary>
    public ScoreAnswer Score(string id) => this[id].AsScore();

    /// <summary>The answer with this id as a <see cref="NumberAnswer"/>.</summary>
    public NumberAnswer Number(string id) => this[id].AsNumber();

    /// <summary>The answer with this id as a <see cref="PointAnswer"/>.</summary>
    public PointAnswer Point(string id) => this[id].AsPoint();

    /// <summary>The answer with this id as a <see cref="BoxAnswer"/>.</summary>
    public BoxAnswer Box(string id) => this[id].AsBox();
}

/// <summary>Token counts of a decision, what was read of its videos, and its booster calls.</summary>
/// <param name="InputTokens">Tokens in the request, images, videos and booster readings included.</param>
/// <param name="OutputTokens">Tokens generated.</param>
/// <param name="VideoFrames">The frames read over all videos; <c>null</c> for a request without videos.</param>
/// <param name="VideoSeconds">The seconds of the video windows read, as received; <c>null</c> for a request without videos.</param>
/// <param name="BoosterTokens">The share of <paramref name="InputTokens"/> the boosters' readings take; <c>null</c> for a request without boosters.</param>
/// <param name="Boosters">The booster calls billed; <c>null</c> for a request without boosters.</param>
/// <param name="Cost">What the answer cost, as the API sent it; <c>null</c> when the response does not say.</param>
public sealed record Usage(long? InputTokens, long? OutputTokens, long? VideoFrames = null, double? VideoSeconds = null, long? BoosterTokens = null, long? Boosters = null, Cost? Cost = null);

/// <summary>
/// What one answer cost (<c>usage.cost</c>), every amount in USD exactly as the API sent it: the
/// amount booked, never summed or recomputed by the client. A value that is absent or of another
/// type is <c>null</c>, never 0.
/// </summary>
/// <param name="Usd">The amount booked for the answer: its model part plus its boosters part, as the API booked it.</param>
/// <param name="ModelUsd">The model's part; 0 when the free tier covered the request.</param>
/// <param name="BoostersUsd">The boosters' part; 0 for an answer without billed booster calls.</param>
/// <param name="FreeTier"><c>true</c> when the free tier covered the model's part.</param>
public sealed record Cost(double? Usd, double? ModelUsd, double? BoostersUsd, bool? FreeTier)
{
    /// <summary>The cost object exactly as received.</summary>
    public JsonObject? Raw { get; init; }

    /// <summary>Two costs are equal when their four values are.</summary>
    public bool Equals(Cost? other) =>
        other is not null && Usd == other.Usd && ModelUsd == other.ModelUsd && BoostersUsd == other.BoostersUsd && FreeTier == other.FreeTier;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Usd, ModelUsd, BoostersUsd, FreeTier);

    internal static Cost? Of(JsonNode? node)
    {
        if (node is not JsonObject cost)
        {
            return null;
        }
        double? Amount(string key) => JsonValues.TryDouble(cost[key], out var d) ? d : null;
        bool? free = cost["free_tier"] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;
        return new Cost(Amount("usd"), Amount("model_usd"), Amount("boosters_usd"), free) { Raw = cost };
    }
}

/// <summary>A model listed by <c>GET /v1/models</c>.</summary>
public sealed class ModelInfo
{
    internal ModelInfo(JsonObject raw)
    {
        Raw = raw;
        Name = JsonValues.AsString(raw["name"]);
        Description = JsonValues.AsString(raw["description"]);
        ReleaseDate = JsonValues.AsString(raw["release_date"]);
        var limits = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            if (key is not ("name" or "description" or "release_date"))
            {
                limits[key] = value?.DeepClone();
            }
        }
        Limits = new ReadOnlyDictionary<string, JsonNode?>(limits);
    }

    /// <summary>
    /// The model id to send: a family id (<c>decisionnode-moe</c>, <c>decisionnode-flash</c>), a
    /// pinned id <c>decisionnode-&lt;family&gt;-&lt;version&gt;</c>, or a fine-tuned model's name.
    /// </summary>
    public string? Name { get; }

    public string? Description { get; }

    /// <summary>The release date as sent, for example <c>2026-10-01</c>.</summary>
    public string? ReleaseDate { get; }

    /// <summary>Every other field of the entry: the model's published limits.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Limits { get; }

    /// <summary>The token limit of state plus questions (<c>context_tokens</c>).</summary>
    public long? ContextTokens => Long("context_tokens");

    /// <summary>Images per request (<c>max_images</c>).</summary>
    public long? MaxImages => Long("max_images");

    /// <summary>The largest image or upload in bytes (<c>upload_max_bytes</c>).</summary>
    public long? UploadMaxBytes => Long("upload_max_bytes");

    /// <summary>How long an upload can be named, in seconds (<c>upload_ttl_seconds</c>).</summary>
    public long? UploadTtlSeconds => Long("upload_ttl_seconds");

    /// <summary>The media types an upload takes (<c>upload_media_types</c>).</summary>
    public IReadOnlyList<string>? UploadMediaTypes =>
        (Raw["upload_media_types"] as JsonArray)?.Select(JsonValues.AsString).OfType<string>().ToList();

    /// <summary>Grid values answered as one question (<c>number_one_question_values</c>).</summary>
    public long? NumberOneQuestionValues => Long("number_one_question_values");

    /// <summary>The most values a number grid may hold (<c>number_max_grid_values</c>).</summary>
    public long? NumberMaxGridValues => Long("number_max_grid_values");

    /// <summary><c>min</c> and <c>max</c> of a number question that leaves them out (<c>number_default_range</c>).</summary>
    public IReadOnlyList<double>? NumberDefaultRange =>
        (Raw["number_default_range"] as JsonArray)?.Select(n => JsonValues.TryDouble(n, out var d) ? d : (double?)null).OfType<double>().ToList();

    /// <summary><c>step</c> of a number question that leaves it out (<c>number_default_step</c>).</summary>
    public double? NumberDefaultStep => JsonValues.TryDouble(Raw["number_default_step"], out var d) ? d : null;

    /// <summary>The model's session limits (<c>sessions</c>: <c>max_per_key</c>, <c>max_window</c>, <c>max_frame_tokens</c> and the rest).</summary>
    public JsonObject? Sessions => Raw["sessions"] as JsonObject;

    /// <summary>The model's Batch API limits (<c>batch</c>: <c>requests_per_batch</c>, <c>add_bytes</c>, <c>price_factor</c> and the rest).</summary>
    public JsonObject? Batch => Raw["batch"] as JsonObject;

    /// <summary><c>true</c> for a fine-tuned model of the key's workspace; <c>null</c> on a catalogue entry.</summary>
    public bool? FineTuned => Raw["fine_tuned"] is JsonValue v && v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? v.GetValue<bool>() : null;

    /// <summary>For a fine-tuned model, the pinned id of the model it was trained from (<c>base</c>).</summary>
    public string? Base => JsonValues.AsString(Raw["base"]);

    /// <summary>For a fine-tuned model, its version number (<c>adapter_version</c>); on a plain name, the version it resolves to.</summary>
    public long? AdapterVersion => Long("adapter_version");

    /// <summary>For a fine-tuned model, <c>deployed</c> (answers decisions), <c>ready</c> (trained, not deployed) or a newer value, as received.</summary>
    public string? Status => JsonValues.AsString(Raw["status"]);

    private long? Long(string field) => JsonValues.AsLong(Raw[field]);

    /// <summary>The entry exactly as received.</summary>
    public JsonObject Raw { get; }
}

/// <summary>Which model answered a decision in place of the one asked for: <see cref="Decision.Fallback"/>.</summary>
/// <param name="ServedModel">The model that answered, from <c>x-decisionnode-fallback</c>.</param>
/// <param name="RequestedModel">The model the request asked for, from <c>x-decisionnode-requested-model</c>; <c>null</c> when the header is absent.</param>
public sealed record Fallback(string ServedModel, string? RequestedModel);

/// <summary>One request for <see cref="DecisionNodeClient.DecideManyAsync"/>, or for <see cref="DecisionNodeClient.DecideAsync(DecideRequest, CancellationToken)"/>.</summary>
public sealed class DecideRequest
{
    /// <summary>What is judged: a string or any JSON-serialisable value. <c>null</c> sends an empty state.</summary>
    public object? State { get; init; }

    /// <summary>The questions, by id. At least one.</summary>
    public required IReadOnlyDictionary<string, Question> Questions { get; init; }

    /// <summary>Images sent beside the state, up to 16.</summary>
    public IReadOnlyList<Image>? Images { get; init; }

    /// <summary>Videos sent as frames beside the state, up to 4.</summary>
    public IReadOnlyList<Video>? Videos { get; init; }

    /// <summary>The model for this request, a fine-tuned model's name included; the client's model when <c>null</c> or empty.</summary>
    public string? Model { get; init; }

    /// <summary>Boosters that read the request's images before the decision, up to 4, in order; none when <c>null</c> or empty.</summary>
    public IReadOnlyList<Booster>? Boosters { get; init; }

    /// <summary><c>false</c>: no other model answers when the one asked for has no ready worker. Left out when <c>null</c>.</summary>
    public bool? Fallback { get; init; }
}

/// <summary>The outcome of one request of <see cref="DecisionNodeClient.DecideManyAsync"/>: a decision or the error it ended with.</summary>
public sealed class DecisionResult
{
    internal DecisionResult(Decision? decision, DecisionNodeError? error)
    {
        Decision = decision;
        Error = error;
    }

    /// <summary>The decision, or <c>null</c> when the request failed.</summary>
    public Decision? Decision { get; }

    /// <summary>The error the request ended with, or <c>null</c> when it succeeded.</summary>
    public DecisionNodeError? Error { get; }

    public bool IsSuccess => Error is null;

    /// <summary>The decision, or the request's error thrown again.</summary>
    public Decision GetDecisionOrThrow()
    {
        if (Decision is not null)
        {
            return Decision;
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(Error!);
        return null!;
    }
}
