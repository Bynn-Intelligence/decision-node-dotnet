using System.Collections.ObjectModel;
using System.Globalization;
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
        var usage = body["usage"] as JsonObject;
        Usage = new Usage(JsonValues.AsLong(usage?["input_tokens"]), JsonValues.AsLong(usage?["output_tokens"]));
        RequestId = header("x-request-id");
        LatencyMs = double.TryParse(header("x-decisionnode-latency-ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : null;
        Safety = Safety.Parse(header("x-decisionnode-safety"));
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

    /// <summary>The model version that answered, for example <c>decisionnode-1.0</c>.</summary>
    public string? Model { get; }

    /// <summary>The tokens the request used.</summary>
    public Usage Usage { get; }

    /// <summary>The <c>x-request-id</c> response header.</summary>
    public string? RequestId { get; }

    /// <summary>The server's processing time in milliseconds, from the <c>x-decisionnode-latency-ms</c> header.</summary>
    public double? LatencyMs { get; }

    /// <summary>The safety check's result, or <c>null</c> when the server sent none (a self-hosted server).</summary>
    public Safety? Safety { get; }

    /// <summary>The parsed response body, including any field this version of the library does not know.</summary>
    public JsonObject Raw { get; }

    /// <summary>The answer with this id as a <see cref="TruthAnswer"/>.</summary>
    public TruthAnswer Truth(string id) => this[id].AsTruth();

    /// <summary>The answer with this id as a <see cref="ChoiceAnswer"/>.</summary>
    public ChoiceAnswer Choice(string id) => this[id].AsChoice();

    /// <summary>The answer with this id as a <see cref="ScoreAnswer"/>.</summary>
    public ScoreAnswer Score(string id) => this[id].AsScore();
}

/// <summary>Token counts of a decision.</summary>
/// <param name="InputTokens">Tokens in the request, images included.</param>
/// <param name="OutputTokens">Tokens generated.</param>
public sealed record Usage(long? InputTokens, long? OutputTokens);

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

    /// <summary>The model id to send, for example <c>decisionnode-1.0</c>.</summary>
    public string? Name { get; }

    public string? Description { get; }

    /// <summary>The release date as sent, for example <c>2026-10-01</c>.</summary>
    public string? ReleaseDate { get; }

    /// <summary>Every other field of the entry: the model's published limits.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Limits { get; }

    /// <summary>The entry exactly as received.</summary>
    public JsonObject Raw { get; }
}

/// <summary>One request for <see cref="DecisionNodeClient.DecideManyAsync"/>, or for <see cref="DecisionNodeClient.DecideAsync(DecideRequest, CancellationToken)"/>.</summary>
public sealed class DecideRequest
{
    /// <summary>What is judged: a string or any JSON-serialisable value. <c>null</c> sends an empty state.</summary>
    public object? State { get; init; }

    /// <summary>The questions, by id. At least one.</summary>
    public required IReadOnlyDictionary<string, Question> Questions { get; init; }

    /// <summary>Images sent beside the state.</summary>
    public IReadOnlyList<Image>? Images { get; init; }

    /// <summary>The model for this request; the client's model when <c>null</c> or empty.</summary>
    public string? Model { get; init; }
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
