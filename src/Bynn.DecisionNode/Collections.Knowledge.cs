using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

public sealed partial class CollectionsClient
{
    /// <summary>The default time <see cref="WaitUntilReadyAsync"/> waits: 300 seconds.</summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(300);

    /// <summary>The default first wait between <see cref="WaitUntilReadyAsync"/>'s reads: 1 second.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>The shortest poll interval <see cref="WaitUntilReadyAsync"/> takes.</summary>
    internal static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(0.1);

    /// <summary>The longest wait between reads that the growing interval reaches.</summary>
    internal static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Adds a document to a knowledge base (<c>POST /v1/collections/{id}/items</c>) and returns
    /// the item as sent: <c>queued</c>, or with <paramref name="wait"/> <c>ready</c> once it is,
    /// within the server's 10 seconds, else as it stands. Ingestion is asynchronous; see
    /// <see cref="WaitUntilReadyAsync"/>.
    /// </summary>
    /// <remarks>
    /// Text goes inline; a small UTF-8 text or Markdown file goes inline as text, titled by its
    /// file name without the extension unless a title is given; any other file is uploaded first
    /// (<c>POST /v1/uploads</c> with the knowledge purpose, then its <c>PUT</c>) and named by
    /// <c>upload_id</c>, the server taking its file name as the default title. The call carries
    /// an <c>Idempotency-Key</c>, the same on every attempt. When the upload this call placed
    /// can no longer be named (expired, consumed, gone or incomplete) the document is uploaded
    /// again and the call sent once more with a new key; a second such answer is thrown.
    /// </remarks>
    /// <param name="collectionId">The knowledge base's id.</param>
    /// <param name="document">The document's content.</param>
    /// <param name="title">The title, 1 to 200 characters; required for text, and for a file sent inline without a file name.</param>
    /// <param name="reference">Your own id for the document, 1 to 64 of <c>A-Z a-z 0-9 _ . : -</c>, unique in the knowledge base; the item's id when left out.</param>
    /// <param name="pinned"><c>true</c>: always given to a request that names the knowledge base, for a rule that must always apply.</param>
    /// <param name="pinRank">A pinned document's rank, 0 to 1,000, lowest first.</param>
    /// <param name="label">A label of 1 to 128 characters.</param>
    /// <param name="metadata">Up to 20 keys, each a text, a number or a boolean; the scope key's value scopes the document (absent, it is global).</param>
    /// <param name="wait"><c>true</c> sends <c>?wait=true</c>: the server holds the call up to 10 seconds for the document to be ready (20 seconds an attempt unless a timeout was set).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ValidationError">A title, format, reference, pin rank, label or metadata out of its bounds, a missing title, or an empty text; nothing is sent.</exception>
    /// <exception cref="BadRequestError">A file that is empty, over 50 MB, or of a type that is not a document's or cannot be told; nothing is sent. Or the API's 400, such as <c>upload_purpose_mismatch</c>.</exception>
    public Task<JsonObject> AddDocumentAsync(
        string collectionId,
        Document document,
        string? title = null,
        string? reference = null,
        bool? pinned = null,
        int? pinRank = null,
        string? label = null,
        JsonObject? metadata = null,
        bool wait = false,
        CancellationToken cancellationToken = default) =>
        WriteDocumentAsync(HttpMethod.Post, Path(collectionId) + "/items", document, new DocumentFields(title, reference, pinned, pinRank, label, metadata), wait, cancellationToken);

    /// <summary>
    /// Replaces a document whole (<c>PUT /v1/collections/{id}/items/{itemId}</c>): a new text,
    /// format or title is a new version, the old one serving until the new one is ready; the same
    /// text again changes nothing. Sent as <see cref="AddDocumentAsync"/> sends a document, and
    /// returns the item as sent.
    /// </summary>
    /// <exception cref="ValidationError">As <see cref="AddDocumentAsync"/>; nothing is sent.</exception>
    /// <exception cref="BadRequestError">As <see cref="AddDocumentAsync"/>.</exception>
    public Task<JsonObject> ReplaceDocumentAsync(
        string collectionId,
        string itemId,
        Document document,
        string? title = null,
        string? reference = null,
        bool? pinned = null,
        int? pinRank = null,
        string? label = null,
        JsonObject? metadata = null,
        bool wait = false,
        CancellationToken cancellationToken = default) =>
        WriteDocumentAsync(HttpMethod.Put, ItemPath(collectionId, itemId), document, new DocumentFields(title, reference, pinned, pinRank, label, metadata), wait, cancellationToken);

    /// <summary>
    /// One item (<c>GET /v1/collections/{id}/items/{itemId}</c>), as sent. With
    /// <paramref name="includeText"/> a document's stored text comes with it (<c>?include=text</c>),
    /// <c>null</c> while no version serves: the text that passages' byte offsets index, in UTF-8.
    /// </summary>
    public Task<JsonObject> GetItemAsync(string collectionId, string itemId, bool includeText = false, CancellationToken cancellationToken = default) =>
        ObjectAsync(HttpMethod.Get, ItemPath(collectionId, itemId) + (includeText ? "?include=text" : string.Empty), null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);

    /// <summary>
    /// A page of a document's live chunks (<c>GET /v1/collections/{id}/items/{itemId}/chunks</c>),
    /// as sent: <c>chunks</c> (each <c>chunk</c>, <c>heading</c>, <c>byte_start</c>,
    /// <c>byte_end</c>, <c>page_start</c>, <c>page_end</c>, <c>tokens</c>, <c>text</c>),
    /// <c>offset</c>, <c>limit</c>, <c>total</c> and <c>next_offset</c>. Each argument is sent only when given.
    /// </summary>
    /// <exception cref="BadRequestError">An offset below 0 or a limit outside 1 to 100; nothing is sent.</exception>
    public Task<JsonObject> ListChunksAsync(string collectionId, string itemId, int? offset = null, int? limit = null, CancellationToken cancellationToken = default)
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
        var path = ItemPath(collectionId, itemId) + "/chunks" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return ObjectAsync(HttpMethod.Get, path, null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);
    }

    /// <summary>
    /// Deletes every failed document of a knowledge base
    /// (<c>DELETE /v1/collections/{id}/items?status=failed</c>) and returns the API's answer as
    /// sent, <c>{"deleted": n}</c>, n the documents deleted (0 when there were none). Idempotent,
    /// so retried as any read. A failed document counts toward nothing, but stays listed with its
    /// reason until it is deleted.
    /// </summary>
    public Task<JsonObject> DeleteFailedAsync(string collectionId, CancellationToken cancellationToken = default) =>
        ObjectAsync(HttpMethod.Delete, Path(collectionId) + "/items?status=failed", null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);

    /// <summary>
    /// Waits on the client for a document, or for a whole knowledge base, to finish ingesting.
    /// With <paramref name="itemId"/> it reads the item until its <c>status</c> is <c>ready</c>
    /// or <c>failed</c> and returns it, either way: a failed item is a value, its <c>error</c>
    /// saying why. Without it, it reads the collection until its <c>status_counts</c> hold no
    /// <c>queued</c> and no <c>processing</c> item, and returns the collection.
    /// </summary>
    /// <remarks>
    /// The timeout is a wall-clock deadline from the call. The first read is made at once; then it waits <paramref name="pollInterval"/>, 1.5 times
    /// longer after each read up to 10 seconds, or the <c>Retry-After</c> of a <c>429</c> or
    /// <c>529</c> answer when that is longer. Each read is retried as any call. A status this
    /// library does not know is waited on.
    /// </remarks>
    /// <param name="collectionId">The knowledge base's id.</param>
    /// <param name="itemId">The document to wait for; the whole knowledge base when <c>null</c>.</param>
    /// <param name="timeout">How long to wait in all; above zero, default 300 seconds.</param>
    /// <param name="pollInterval">The first wait between reads; at least 0.1 seconds, default 1 second.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="ArgumentOutOfRangeException">A timeout that is not above zero, or a poll interval under 0.1 seconds; nothing is sent.</exception>
    /// <exception cref="TimeoutException">Not ready or failed within <paramref name="timeout"/>.</exception>
    public async Task<JsonObject> WaitUntilReadyAsync(string collectionId, string? itemId = null, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? DefaultWaitTimeout;
        var interval = pollInterval ?? DefaultPollInterval;
        if (limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), limit, "timeout must be above zero.");
        }
        if (interval < MinPollInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), interval, "pollInterval must be at least 0.1 seconds.");
        }
        var longest = interval > MaxPollInterval ? interval : MaxPollInterval;
        var path = itemId is null ? Path(collectionId) : ItemPath(collectionId, itemId);
        var clock = Stopwatch.StartNew();
        var waited = TimeSpan.Zero;
        while (true)
        {
            TimeSpan? asked = null;
            var plan = new DecisionNodeClient.AttemptPlan(null, true, null, w => asked = asked is { } a && a > w ? a : w);
            var current = await ObjectAsync(HttpMethod.Get, path, null, DecisionNodeClient.RetryPolicy.Default, cancellationToken, plan: plan).ConfigureAwait(false);
            if (itemId is null ? Settled(current) : Finished(current))
            {
                return current;
            }
            // The time spent: the clock's, or the waits scheduled when they are not spent in full.
            var spent = clock.Elapsed > waited ? clock.Elapsed : waited;
            var left = limit - spent;
            if (left <= TimeSpan.Zero)
            {
                var what = itemId is null ? $"collection {collectionId}" : $"item {itemId}";
                throw new TimeoutException($"the {what} was not ready within {limit.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
            }
            var pause = asked is { } server && server > interval ? server : interval;
            if (pause > left)
            {
                pause = left;
            }
            await _client.Delay(pause, cancellationToken).ConfigureAwait(false);
            waited += pause;
            var next = interval * 1.5;
            interval = next > longest ? longest : next;
        }
    }

    /// <summary>An item whose status is <c>ready</c> or <c>failed</c>.</summary>
    private static bool Finished(JsonObject item) => JsonValues.AsString(item["status"]) is "ready" or "failed";

    /// <summary>A collection with no item queued or processing, each count 0 or absent.</summary>
    private static bool Settled(JsonObject collection)
    {
        var counts = collection["status_counts"] as JsonObject;
        static bool None(JsonNode? count) => count is null || JsonValues.AsLong(count) == 0;
        return None(counts?["queued"]) && None(counts?["processing"]);
    }

    /// <summary>
    /// Searches a knowledge base without a decision (<c>POST /v1/collections/{id}/search</c>) and
    /// returns its reading, every passage with its text. Billed as one knowledge call; needs a
    /// full-scope key. Every argument but <paramref name="query"/> is sent only when given, each
    /// as given: nothing is checked before sending, the API answers a bad option with its 400.
    /// </summary>
    /// <param name="collectionId">The knowledge base's id.</param>
    /// <param name="query">What to search for, 1 to 2,000 characters.</param>
    /// <param name="scope">The value of the knowledge base's scope key.</param>
    /// <param name="match">Metadata equality, any JSON-serialisable value.</param>
    /// <param name="filter">A typed filter tree, any JSON-serialisable value.</param>
    /// <param name="topK">The most passages, pins not counted, 0 to 20.</param>
    /// <param name="pinned"><c>false</c> leaves the pinned documents out.</param>
    /// <param name="maxTokens">The passages' token budget: an integer from 128 to 8,192, or <c>"auto"</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="PermissionDeniedError">The key is not full-scope (<c>key_scope_denied</c>).</exception>
    public async Task<KnowledgeReading> SearchAsync(
        string collectionId,
        string? query,
        string? scope = null,
        object? match = null,
        object? filter = null,
        int? topK = null,
        bool? pinned = null,
        object? maxTokens = null,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject();
        void Add(string key, object? value)
        {
            if (value is not null)
            {
                body[key] = JsonValues.ToNode(value, $"the search's '{key}'");
            }
        }
        Add("query", query);
        Add("scope", scope);
        Add("match", match);
        Add("filter", filter);
        Add("top_k", topK);
        Add("pinned", pinned);
        Add("max_tokens", maxTokens);
        var path = Path(collectionId) + "/search";
        var answer = await ObjectAsync(HttpMethod.Post, path, body, DecisionNodeClient.RetryPolicy.Default, cancellationToken).ConfigureAwait(false);
        return Reading.Parse(Booster.KnowledgeName, answer) as KnowledgeReading ?? throw new ServerError(new ErrorDetails
        {
            Message = $"the response of POST {path} is not a knowledge reading",
            Body = answer,
        });
    }

    /// <summary>The fields of a document's item beside its content, each sent only when given.</summary>
    private sealed record DocumentFields(string? Title, string? Reference, bool? Pinned, int? PinRank, string? Label, JsonObject? Metadata);

    /// <summary>
    /// A document's <c>POST</c> or <c>PUT</c>: its content placed (inline, or uploaded and named by
    /// <c>upload_id</c>), the item's fields checked as the API checks them, and the call sent with
    /// a new <c>Idempotency-Key</c>. A stale reference to the upload this call placed is uploaded
    /// again and sent once more, with a new key.
    /// </summary>
    private async Task<JsonObject> WriteDocumentAsync(HttpMethod method, string path, Document document, DocumentFields fields, bool wait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        DocumentFile? file = null;
        string? text = null;
        string? format = null;
        var title = fields.Title;
        var titleRequired = false;
        if (document.IsText)
        {
            text = document.Text;
            format = document.Format;
            titleRequired = true;
        }
        else if (document.UploadId is null)
        {
            file = await document.ReadFileAsync(0, ct).ConfigureAwait(false);
            if (file.IsText && file.Data.Length < _client.InlineThreshold && file.Utf8Text() is { } decoded)
            {
                // A small text or Markdown file goes inline: one call instead of three.
                text = decoded;
                format = file.ContentType == "text/markdown" ? Document.Markdown : Document.PlainText;
                title ??= file.Stem;
                titleRequired = true;
                file = null;
            }
        }
        LocalChecks.DocumentItem(title, titleRequired, document.IsText ? document.Format : null, document.IsText ? document.Text : null, fields.Reference, fields.PinRank, fields.Label, fields.Metadata);
        if (wait)
        {
            path += "?wait=true";
        }
        var timeout = wait ? _client.WaitCallTimeout : (TimeSpan?)null;
        for (var round = 0; ; round++)
        {
            string? placed = null;
            JsonObject body;
            if (text is not null)
            {
                body = new JsonObject { ["title"] = title, ["text"] = text };
                if (format is not null)
                {
                    body["format"] = format;
                }
            }
            else
            {
                placed = file is null ? null : (await _client.UploadDocumentFilesAsync(new[] { file }, ct).ConfigureAwait(false))[0];
                body = new JsonObject { ["upload_id"] = placed ?? document.UploadId };
                if (fields.Title is not null)
                {
                    body["title"] = fields.Title;
                }
            }
            if (fields.Reference is not null)
            {
                body["reference"] = fields.Reference;
            }
            if (fields.Pinned is { } pinned)
            {
                body["pinned"] = pinned;
            }
            if (fields.PinRank is { } pinRank)
            {
                body["pin_rank"] = pinRank;
            }
            if (fields.Label is not null)
            {
                body["label"] = fields.Label;
            }
            if (fields.Metadata is not null)
            {
                body["metadata"] = fields.Metadata.DeepClone();
            }
            try
            {
                return await ObjectAsync(method, path, body, DecisionNodeClient.RetryPolicy.Idempotent, ct, NewKey(), timeout).ConfigureAwait(false);
            }
            catch (BadRequestError error) when (round == 0 && placed is not null && error.ErrorType is { } type && DecisionNodeClient.StaleUpload.Contains(type))
            {
                // The upload this call placed can no longer be named: upload the document again.
            }
        }
    }
}
