using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

// Some tests set DECISIONNODE_* environment variables, which every client reads.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Bynn.DecisionNode.Tests;

/// <summary>A request as the stub handler saw it.</summary>
public sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body)
{
    public JsonNode? Json => Body is null ? null : JsonNode.Parse(Body);
}

/// <summary>
/// An in-memory HTTP handler: answers from a function and records every request. It is a
/// <see cref="DelegatingHandler"/> over a handler that never follows redirects and never
/// passes a request on, so a client built on it sets
/// <see cref="DecisionNodeClientOptions.DelegatingHandlersNeverResendRequests"/>.
/// </summary>
public sealed class StubHandler : DelegatingHandler
{
    private readonly Func<Recorded, int, CancellationToken, Task<HttpResponseMessage>> _respond;
    private int _count;

    public StubHandler(Func<Recorded, int, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
        InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
    }

    public StubHandler(Func<Recorded, int, HttpResponseMessage> respond)
        : this((r, i, _) => Task.FromResult(respond(r, i)))
    {
    }

    public ConcurrentQueue<Recorded> Requests { get; } = new();

    public int InFlight;
    public int MaxInFlight;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in request.Headers.NonValidated)
        {
            headers[k] = string.Join(", ", v);
        }
        string? body = null;
        if (request.Content is not null)
        {
            foreach (var (k, v) in request.Content.Headers.NonValidated)
            {
                headers[k] = string.Join(", ", v);
            }
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }
        var rec = new Recorded(request.Method, request.RequestUri!, headers, body);
        Requests.Enqueue(rec);
        var n = Interlocked.Increment(ref _count) - 1;
        var now = Interlocked.Increment(ref InFlight);
        int seen;
        while ((seen = MaxInFlight) < now && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen)
        {
        }
        try
        {
            return await _respond(rec, n, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref InFlight);
        }
    }
}

public static class Http
{
    public const string TruthBody = """{"model":"decisionnode-moe-0.9.0-rc1","answers":{"q":{"type":"truth","truth":0.5}},"usage":{"input_tokens":9,"output_tokens":0}}""";

    public static HttpResponseMessage Json(int status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return response;
    }

    public static HttpResponseMessage Ok(string body = TruthBody, params (string Name, string Value)[] headers) => Json(200, body, headers);

    public static DecisionNodeClient Client(StubHandler handler, int? maxRetries = null, TimeSpan? timeout = null, string? apiKey = "dn_test_key", IDictionary<string, string>? headers = null, string? model = null, Action<string>? log = null) =>
        new(new DecisionNodeClientOptions
        {
            ApiKey = apiKey,
            BaseUrl = "https://api.example.test",
            Model = model,
            MaxRetries = maxRetries,
            Timeout = timeout,
            Headers = headers,
            HttpClient = new HttpClient(handler),
            DelegatingHandlersNeverResendRequests = true,
            DebugLog = log,
        });

    public static Dictionary<string, Question> OneTruth(string text = "?") => new() { ["q"] = Question.Truth(text) };
}
