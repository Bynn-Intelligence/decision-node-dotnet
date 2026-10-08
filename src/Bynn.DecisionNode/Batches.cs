using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

public sealed partial class DecisionNodeClient
{
    /// <summary>The shortest poll interval <see cref="RunBatchAsync"/> takes.</summary>
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs a batch end to end: creates an empty draft, adds the requests (in calls of at
    /// most 10 MiB), finalizes it, polls <c>GET /v1/batches/{id}</c> every
    /// <paramref name="pollInterval"/> (or the server's <c>Retry-After</c> when longer) until
    /// it ends, and returns the ended batch with its results. If an add fails, the draft is
    /// cancelled and the add's error thrown.
    /// </summary>
    /// <param name="requests">The requests, each with its own <see cref="BatchRequest.CustomId"/>.</param>
    /// <param name="pollInterval">The fixed wait between polls; default 60 seconds, at least 10.</param>
    /// <param name="timeout">How long to wait for the batch to end; default 26 hours.</param>
    /// <param name="cancellationToken">Cancels the call and the polling (the batch keeps running).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pollInterval"/> is below 10 seconds; nothing is sent.</exception>
    /// <exception cref="TimeoutException">The batch did not end within <paramref name="timeout"/>.</exception>
    public async Task<BatchRun> RunBatchAsync(
        IEnumerable<BatchRequest> requests,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var interval = pollInterval ?? TimeSpan.FromSeconds(60);
        if (interval < MinPollInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), interval, "pollInterval must be at least 10 seconds.");
        }
        var limit = timeout ?? TimeSpan.FromHours(26);
        if (limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), limit, "timeout must be above zero.");
        }
        // Every line is built and checked first, so a fault fails before the draft exists.
        var prepared = Batches.Prepare(requests, allowEmpty: true);

        var draft = await Batches.CreateAsync(Array.Empty<BatchRequest>(), cancellationToken).ConfigureAwait(false);
        if (prepared.Count > 0)
        {
            try
            {
                var lines = await Batches.EncodeAsync(prepared, cancellationToken).ConfigureAwait(false);
                await Batches.AddLinesAsync(draft.Id, lines, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Batches.CancelAsync(draft.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (DecisionNodeError)
                {
                    // The add's error is the one that matters.
                }
                throw;
            }
        }
        var batch = await Batches.FinalizeAsync(draft.Id, cancellationToken).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var waited = TimeSpan.Zero;
        Func<string, string?> lastHeaders = _ => null;
        while (batch.EndedAt is null)
        {
            var spent = clock.Elapsed > waited ? clock.Elapsed : waited;
            if (spent >= limit)
            {
                throw new TimeoutException($"the batch {batch.Id} did not end within {limit}");
            }
            var wait = interval;
            if (RetryWait(lastHeaders) is { } server && server > wait)
            {
                wait = server;
            }
            await Delay(wait, cancellationToken).ConfigureAwait(false);
            waited += wait;
            (batch, lastHeaders) = await Batches.GetWithHeadersAsync(batch.Id, cancellationToken).ConfigureAwait(false);
        }
        return new BatchRun(batch, Batches.ResultsAsync(batch.Id, cancellationToken));
    }
}

/// <summary>
/// The Batch API (<see cref="DecisionNodeClient.Batches"/>): many decisions answered within
/// 24 hours at half the price. Every call is safe to retry except create, add and
/// finalize, which are retried only on <c>429</c> and <c>529</c>.
/// </summary>
public sealed class BatchesClient
{
    /// <summary>The most bytes one add call carries; a larger list is split into several calls, in order.</summary>
    public const int MaxAddBytes = 10 * 1024 * 1024;

    private const string NdJson = "application/x-ndjson";
    private readonly DecisionNodeClient _client;

    internal BatchesClient(DecisionNodeClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Creates a batch (<c>POST /v1/batches</c>) as a draft holding <paramref name="requests"/>,
    /// sent as JSON Lines. With no requests the draft is empty; add to it, then finalize it.
    /// </summary>
    /// <exception cref="BadRequestError">A request breaks a fixed rule of the API; nothing is sent.</exception>
    public async Task<Batch> CreateAsync(IEnumerable<BatchRequest> requests, CancellationToken cancellationToken = default)
    {
        var lines = await EncodeAsync(Prepare(requests, allowEmpty: true), cancellationToken).ConfigureAwait(false);
        var body = Join(lines, 0, lines.Count);
        var (parsed, result) = await _client.SendJsonAsync(HttpMethod.Post, "/v1/batches", body, NdJson, DecisionNodeClient.RetryPolicy.Mutation, cancellationToken).ConfigureAwait(false);
        return BatchOf(parsed, result);
    }

    /// <summary>
    /// Adds requests to a draft (<c>POST /v1/batches/{id}/requests</c>), in calls of at most
    /// 10 MiB, in order. Returns the batch after the last call.
    /// </summary>
    /// <exception cref="BadRequestError">No requests, or a request breaks a fixed rule; nothing is sent.</exception>
    public async Task<Batch> AddAsync(string batchId, IEnumerable<BatchRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        var prepared = Prepare(requests, allowEmpty: false);
        var lines = await EncodeAsync(prepared, cancellationToken).ConfigureAwait(false);
        return await AddLinesAsync(batchId, lines, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<Batch> AddLinesAsync(string batchId, IReadOnlyList<byte[]> lines, CancellationToken ct)
    {
        Batch? last = null;
        var start = 0;
        while (start < lines.Count)
        {
            var end = start;
            long size = 0;
            while (end < lines.Count && (end == start || size + lines[end].Length <= MaxAddBytes))
            {
                size += lines[end].Length;
                end++;
            }
            var (parsed, result) = await _client.SendJsonAsync(HttpMethod.Post, Path(batchId, "/requests"), Join(lines, start, end), NdJson, DecisionNodeClient.RetryPolicy.Mutation, ct).ConfigureAwait(false);
            last = BatchOf(parsed, result);
            start = end;
        }
        return last!;
    }

    /// <summary>
    /// Finalizes a draft (<c>POST /v1/batches/{id}/finalize</c>) so it runs. A <c>409</c>
    /// <c>invalid_batch_state</c> is read with <see cref="GetAsync"/>: when the batch is no
    /// longer a draft (an earlier finalize went through), the batch is returned.
    /// </summary>
    public async Task<Batch> FinalizeAsync(string batchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        try
        {
            var (parsed, result) = await _client.SendJsonAsync(HttpMethod.Post, Path(batchId, "/finalize"), null, null, DecisionNodeClient.RetryPolicy.Mutation, cancellationToken).ConfigureAwait(false);
            return BatchOf(parsed, result);
        }
        catch (DecisionNodeError error) when (error.Status == 409 && error.ErrorType == "invalid_batch_state")
        {
            var batch = await GetAsync(batchId, cancellationToken).ConfigureAwait(false);
            if (batch.State != "draft")
            {
                return batch;
            }
            throw;
        }
    }

    /// <summary>Reads a batch (<c>GET /v1/batches/{id}</c>).</summary>
    public async Task<Batch> GetAsync(string batchId, CancellationToken cancellationToken = default) =>
        (await GetWithHeadersAsync(batchId, cancellationToken).ConfigureAwait(false)).Batch;

    internal async Task<(Batch Batch, Func<string, string?> Header)> GetWithHeadersAsync(string batchId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        var (parsed, result) = await _client.SendJsonAsync(HttpMethod.Get, Path(batchId, string.Empty), null, null, DecisionNodeClient.RetryPolicy.Default, ct).ConfigureAwait(false);
        return (BatchOf(parsed, result), result.Header);
    }

    /// <summary>Lists the key's batches, newest first (<c>GET /v1/batches</c>).</summary>
    /// <param name="offset">The first batch, 0-based; the server's default (0) when <c>null</c>.</param>
    /// <param name="limit">How many, 1 to 100; the server's default (20) when <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the call, retries included.</param>
    /// <exception cref="BadRequestError"><paramref name="offset"/> is below 0 or <paramref name="limit"/> outside 1 to 100; nothing is sent.</exception>
    public async Task<BatchPage> ListAsync(int? offset = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        LocalChecks.ListPage(offset, limit);
        var query = new List<string>();
        if (offset is { } o)
        {
            query.Add("offset=" + o.ToString(CultureInfo.InvariantCulture));
        }
        if (limit is { } l)
        {
            query.Add("limit=" + l.ToString(CultureInfo.InvariantCulture));
        }
        var path = "/v1/batches" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        var (parsed, result) = await _client.SendJsonAsync(HttpMethod.Get, path, null, null, DecisionNodeClient.RetryPolicy.Default, cancellationToken).ConfigureAwait(false);
        if (parsed is not JsonObject page || page["batches"] is not JsonArray)
        {
            throw Malformed(result, parsed, "the response of GET /v1/batches has no 'batches' list");
        }
        return new BatchPage(page);
    }

    /// <summary>
    /// The results of an ended batch (<c>GET /v1/batches/{id}/results</c>), one per request
    /// in upload order, read line by line as they arrive. A <c>409</c> <c>results_not_ready</c>
    /// is a <see cref="DecisionNodeError"/> with that <c>error_type</c>; <c>410</c>
    /// <c>results_expired</c> likewise.
    /// </summary>
    public async IAsyncEnumerable<BatchResult> ResultsAsync(string batchId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        var result = await _client.ExchangeAsync(HttpMethod.Get, Path(batchId, "/results"), null, null, DecisionNodeClient.RetryPolicy.Default, stream: true, cancellationToken).ConfigureAwait(false);
        using var response = result.Response!;
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            JsonObject? raw;
            try
            {
                raw = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException ex)
            {
                throw new ServerError(new ErrorDetails
                {
                    Status = result.Status,
                    RequestId = result.Header("x-request-id"),
                    Message = "a line of the batch results is not JSON",
                    Inner = ex,
                });
            }
            if (raw is null)
            {
                throw Malformed(result, null, "a line of the batch results is not a JSON object");
            }
            yield return new BatchResult(raw);
        }
    }

    /// <summary>
    /// Cancels a batch (<c>POST /v1/batches/{id}/cancel</c>). A draft or a batch with nothing
    /// running ends at once; running lines finish first. Safe to repeat.
    /// </summary>
    public async Task<Batch> CancelAsync(string batchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        var (parsed, result) = await _client.SendJsonAsync(HttpMethod.Post, Path(batchId, "/cancel"), null, null, DecisionNodeClient.RetryPolicy.Default, cancellationToken).ConfigureAwait(false);
        return BatchOf(parsed, result);
    }

    /// <summary>Every request's line, checked by the API's fixed rules, before anything is sent.</summary>
    internal List<(JsonObject Line, List<Image> Images)> Prepare(IEnumerable<BatchRequest> requests, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var list = requests.ToList();
        foreach (var request in list)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(requests));
        }
        // The call's custom_id rules first, then each line as a decide request.
        LocalChecks.BatchLines(list.Select(r => r.CustomId).ToList(), allowEmpty, _client.Held(null));
        var prepared = new List<(JsonObject Line, List<Image> Images)>();
        foreach (var request in list)
        {
            var qs = DecisionNodeClient.QuestionMap(request.Questions, nameof(requests));
            var images = DecisionNodeClient.ImageList(request.Images, nameof(requests));
            var model = string.IsNullOrEmpty(request.Model) ? _client.Model : request.Model;
            var body = new JsonObject
            {
                ["model"] = model,
                ["state"] = DecisionNodeClient.StateNode(request.State),
                ["questions"] = qs,
            };
            var held = _client.Held(model);
            LocalChecks.Request(body, images.Count, LocalChecks.Decide, held);
            LocalChecks.Images(images, held);
            var line = new JsonObject { ["custom_id"] = request.CustomId };
            foreach (var (key, value) in body.ToList())
            {
                body.Remove(key);
                line[key] = value;
            }
            prepared.Add((line, images));
        }
        return prepared;
    }

    /// <summary>
    /// Each line as JSON and a newline. Images are placed per line by the inline threshold;
    /// uploaded ones are held by the batch.
    /// </summary>
    internal async Task<List<byte[]>> EncodeAsync(List<(JsonObject Line, List<Image> Images)> prepared, CancellationToken ct)
    {
        var lines = new List<byte[]>(prepared.Count);
        foreach (var (line, images) in prepared)
        {
            if (images.Count > 0)
            {
                var (wires, _) = await _client.PlaceImagesAsync(images, ct).ConfigureAwait(false);
                line["images"] = wires;
            }
            var bytes = JsonValues.ToUtf8(line);
            var withNewline = new byte[bytes.Length + 1];
            bytes.CopyTo(withNewline, 0);
            withNewline[^1] = (byte)'\n';
            lines.Add(withNewline);
        }
        return lines;
    }

    private static byte[] Join(IReadOnlyList<byte[]> lines, int start, int end)
    {
        var size = 0;
        for (var i = start; i < end; i++)
        {
            size += lines[i].Length;
        }
        var body = new byte[size];
        var at = 0;
        for (var i = start; i < end; i++)
        {
            lines[i].CopyTo(body, at);
            at += lines[i].Length;
        }
        return body;
    }

    private static string Path(string batchId, string suffix) => "/v1/batches/" + Uri.EscapeDataString(batchId) + suffix;

    private static Batch BatchOf(JsonNode? parsed, DecisionNodeClient.Attempt result) =>
        parsed is JsonObject o && JsonValues.AsString(o["id"]) is not null
            ? new Batch(o)
            : throw Malformed(result, parsed, "the response is not a batch object");

    private static ServerError Malformed(DecisionNodeClient.Attempt result, JsonNode? body, string message) => new(new ErrorDetails
    {
        Status = result.Status,
        RequestId = result.Header("x-request-id"),
        Message = message,
        Body = body,
    });
}

/// <summary>One request of a batch: a decide request plus its <see cref="CustomId"/>.</summary>
public sealed class BatchRequest
{
    /// <summary>Your id for this request, unique within the batch, 1 to 512 characters. Results carry it. A missing one is refused locally.</summary>
    public required string CustomId { get; init; }

    /// <summary>What is judged: a string or any JSON-serialisable value. <c>null</c> sends an empty state.</summary>
    public object? State { get; init; }

    /// <summary>The questions, by id. At least one.</summary>
    public required IReadOnlyDictionary<string, Question> Questions { get; init; }

    /// <summary>Images, placed by the client's inline threshold for this line alone.</summary>
    public IReadOnlyList<Image>? Images { get; init; }

    /// <summary>The model for this request; the client's model when <c>null</c> or empty.</summary>
    public string? Model { get; init; }
}

/// <summary>A batch: every field of the batch object.</summary>
public sealed class Batch
{
    internal Batch(JsonObject raw)
    {
        Raw = raw;
        Id = JsonValues.AsString(raw["id"])!;
        State = JsonValues.AsString(raw["state"]);
        Counters = new BatchCounters(raw["counters"] as JsonObject);
        CreatedAt = JsonValues.AsString(raw["created_at"]);
        FinalizedAt = JsonValues.AsString(raw["finalized_at"]);
        ExpiresAt = JsonValues.AsString(raw["expires_at"]);
        EndedAt = JsonValues.AsString(raw["ended_at"]);
        ResultsUrl = JsonValues.AsString(raw["results_url"]);
        ResultsExpireAt = JsonValues.AsString(raw["results_expire_at"]);
        PriceFactor = JsonValues.TryDouble(raw["price_factor"], out var f) ? f : null;
        Usage = Session.UsageOf(raw["usage"]);
        if (raw["error"] is JsonObject error)
        {
            ErrorType = JsonValues.AsString(error["error_type"]);
            ErrorMessage = JsonValues.AsString(error["message"]);
        }
    }

    /// <summary><c>batch_</c> and 10 characters.</summary>
    public string Id { get; }

    /// <summary><c>draft</c>, <c>queued</c>, <c>running</c>, <c>completed</c>, <c>failed</c>, <c>cancelled</c> or <c>expired</c>.</summary>
    public string? State { get; }

    public BatchCounters Counters { get; }

    /// <summary>ISO 8601 UTC, as sent.</summary>
    public string? CreatedAt { get; }

    public string? FinalizedAt { get; }

    /// <summary>The cut-off: 24 hours after finalize.</summary>
    public string? ExpiresAt { get; }

    /// <summary>When the batch ended; <c>null</c> while it runs.</summary>
    public string? EndedAt { get; }

    public string? ResultsUrl { get; }

    /// <summary>When the results are deleted: 7 days after the end.</summary>
    public string? ResultsExpireAt { get; }

    public double? PriceFactor { get; }

    public Usage? Usage { get; }

    /// <summary>A failed batch's <c>error.error_type</c> (<c>batch_failed</c>).</summary>
    public string? ErrorType { get; }

    /// <summary>A failed batch's <c>error.message</c>.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Whether the batch has ended (<see cref="EndedAt"/> is set).</summary>
    public bool HasEnded => EndedAt is not null;

    /// <summary>The batch object exactly as received.</summary>
    public JsonObject Raw { get; }
}

/// <summary>A batch's line counters.</summary>
public sealed class BatchCounters
{
    internal BatchCounters(JsonObject? raw)
    {
        Requests = JsonValues.AsLong(raw?["requests"]) ?? 0;
        Queued = JsonValues.AsLong(raw?["queued"]) ?? 0;
        InProgress = JsonValues.AsLong(raw?["in_progress"]) ?? 0;
        Completed = JsonValues.AsLong(raw?["completed"]) ?? 0;
        Failed = JsonValues.AsLong(raw?["failed"]) ?? 0;
        Expired = JsonValues.AsLong(raw?["expired"]) ?? 0;
        Cancelled = JsonValues.AsLong(raw?["cancelled"]) ?? 0;
    }

    public long Requests { get; }

    public long Queued { get; }

    public long InProgress { get; }

    public long Completed { get; }

    public long Failed { get; }

    public long Expired { get; }

    public long Cancelled { get; }
}

/// <summary>One page of <see cref="BatchesClient.ListAsync"/>.</summary>
public sealed class BatchPage
{
    internal BatchPage(JsonObject raw)
    {
        Raw = raw;
        Batches = ((JsonArray)raw["batches"]!).OfType<JsonObject>().Select(b => new Batch(b)).ToList();
        Offset = JsonValues.AsLong(raw["offset"]) ?? 0;
        Limit = JsonValues.AsLong(raw["limit"]) ?? 0;
        Total = JsonValues.AsLong(raw["total"]) ?? 0;
        NextOffset = JsonValues.AsLong(raw["next_offset"]);
    }

    public IReadOnlyList<Batch> Batches { get; }

    public long Offset { get; }

    public long Limit { get; }

    public long Total { get; }

    /// <summary>The offset of the next page, or <c>null</c> on the last.</summary>
    public long? NextOffset { get; }

    public JsonObject Raw { get; }
}

/// <summary>
/// One line of a batch's results: a <see cref="Decision"/> for a <c>200</c>, the error that
/// status and body give (as a value, never thrown) for any other status, or neither for a
/// line that never ran (<c>expired</c>, <c>cancelled</c>).
/// </summary>
public sealed class BatchResult
{
    internal BatchResult(JsonObject raw)
    {
        Raw = raw;
        CustomId = JsonValues.AsString(raw["custom_id"]);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw["headers"] is JsonObject h)
        {
            foreach (var (name, value) in h)
            {
                if (JsonValues.AsString(value) is { } text)
                {
                    headers[name.ToLowerInvariant()] = text;
                }
            }
        }
        Headers = new ReadOnlyDictionary<string, string>(headers);
        string? Header(string name) => headers.TryGetValue(name, out var v) ? v : null;
        var status = raw["status"];
        Status = JsonValues.AsLong(status) is { } code ? (int)code : null;
        Outcome = JsonValues.AsString(status);
        var body = raw["body"];
        if (Status == 200 && body is JsonObject decision)
        {
            Decision = new Decision(decision, Header, 200);
        }
        else if (Status is { } other)
        {
            Error = DecisionNodeClient.ErrorFor(other, body?.DeepClone(), Header);
        }
    }

    /// <summary>The request's id, as uploaded.</summary>
    public string? CustomId { get; }

    /// <summary>The line's HTTP status, or <c>null</c> for a line that never ran.</summary>
    public int? Status { get; }

    /// <summary><c>expired</c> or <c>cancelled</c> for a line that never ran; <c>null</c> otherwise.</summary>
    public string? Outcome { get; }

    /// <summary>The decision of a <c>200</c> line, numbers as received.</summary>
    public Decision? Decision { get; }

    /// <summary>The error a non-<c>200</c> line's status and body give, with its class and fields.</summary>
    public DecisionNodeError? Error { get; }

    /// <summary>The line's headers, lower-case names.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The line exactly as received.</summary>
    public JsonObject Raw { get; }
}

/// <summary>The outcome of <see cref="DecisionNodeClient.RunBatchAsync"/>: the ended batch and its results.</summary>
public sealed class BatchRun
{
    internal BatchRun(Batch batch, IAsyncEnumerable<BatchResult> results)
    {
        Batch = batch;
        Results = results;
    }

    /// <summary>The batch as it ended.</summary>
    public Batch Batch { get; }

    /// <summary>The results, read from the server line by line as they are enumerated.</summary>
    public IAsyncEnumerable<BatchResult> Results { get; }
}
