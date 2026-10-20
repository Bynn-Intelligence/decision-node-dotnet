using System.Globalization;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// A workspace's collections: of faces (type <c>face</c>), with the public collections it may
/// use, which the <c>face_recognition</c> booster searches; and of documents (type <c>text</c>,
/// a knowledge base), which the <c>knowledge</c> booster reads. Every call returns the API's
/// object exactly as sent; one object serves both types, told apart by its <c>type</c>.
/// <see cref="CreateAsync"/>, <see cref="EnrollAsync"/>, <see cref="AddDocumentAsync"/>,
/// <see cref="ReplaceDocumentAsync"/> and <see cref="UpdateItemAsync"/> send an
/// <c>Idempotency-Key</c>, a new one per call and the same on every attempt of it, so the API
/// creates, enrolls, ingests and bills once however often the call is tried; they are retried as
/// any call, and also on a <c>409</c> <c>idempotency_in_progress</c>. A <c>409</c>
/// <c>idempotency_key_reused</c> is thrown as the base <see cref="DecisionNodeError"/>. The reads
/// and deletes carry no key.
/// </summary>
public sealed partial class CollectionsClient
{
    private readonly DecisionNodeClient _client;

    internal CollectionsClient(DecisionNodeClient client)
    {
        _client = client;
    }

    /// <summary>Creates a collection (<c>POST /v1/collections</c>).</summary>
    /// <param name="name">The collection's name, 1 to 80 characters.</param>
    /// <param name="type">What it holds: <c>face</c> (the default) or <c>text</c>, a knowledge base of documents.</param>
    /// <param name="scopeKey">
    /// A knowledge base's scope key, 1 to 40 of a-z, 0-9 and _, such as <c>mission</c>: the one
    /// metadata key whose value scopes a document to, say, one mission, named by the booster's
    /// <c>scope</c>. Fixed for the collection's life; sent only when given, and only taken with <c>text</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ValidationError">A type other than <c>face</c> or <c>text</c>, a name not 1 to 80 characters, or a scope key not of its pattern; nothing is sent.</exception>
    public async Task<JsonObject> CreateAsync(string name, string type = "face", string? scopeKey = null, CancellationToken cancellationToken = default)
    {
        LocalChecks.CollectionCreate(name, type, scopeKey);
        var body = new JsonObject { ["type"] = type, ["name"] = name };
        if (scopeKey is not null)
        {
            body["scope_key"] = scopeKey;
        }
        return await ObjectAsync(HttpMethod.Post, "/v1/collections", body, DecisionNodeClient.RetryPolicy.Idempotent, cancellationToken, NewKey()).ConfigureAwait(false);
    }

    /// <summary>
    /// The collections the workspace may use (<c>GET /v1/collections</c>), in the order sent:
    /// its own, then the public ones.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> ListAsync(CancellationToken cancellationToken = default)
    {
        var page = await ObjectAsync(HttpMethod.Get, "/v1/collections", null, DecisionNodeClient.RetryPolicy.Default, cancellationToken).ConfigureAwait(false);
        if (page["collections"] is not JsonArray list || list.Any(c => c is not JsonObject))
        {
            throw new ServerError(new ErrorDetails { Message = "the response of GET /v1/collections has no 'collections' list of objects", Body = page });
        }
        return list.Select(c => (JsonObject)c!).ToList();
    }

    /// <summary>One collection (<c>GET /v1/collections/{id}</c>); a public collection is named by its id, such as <c>celebrities</c>.</summary>
    public Task<JsonObject> GetAsync(string collectionId, CancellationToken cancellationToken = default) =>
        ObjectAsync(HttpMethod.Get, Path(collectionId), null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);

    /// <summary>
    /// Deletes a collection and its items (<c>DELETE /v1/collections/{id}</c>). A knowledge base
    /// answers <c>{"id", "deleted": true}</c> once purged within 10 seconds, else
    /// <c>{"id", "status": "purging"}</c> while the purge finishes in the background; both are
    /// success, returned as sent, and the purge is not polled.
    /// </summary>
    public Task<JsonObject> DeleteAsync(string collectionId, CancellationToken cancellationToken = default) =>
        ObjectAsync(HttpMethod.Delete, Path(collectionId), null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);

    /// <summary>
    /// Enrolls one face from one image (<c>POST /v1/collections/{id}/items</c>). The image is
    /// sent as a decision's is: a URL as given, local bytes inline under the inline threshold or
    /// else uploaded.
    /// </summary>
    /// <param name="collectionId">The collection's id.</param>
    /// <param name="image">The picture of the face.</param>
    /// <param name="reference">Your own id for the person, 1 to 64 of <c>A-Z a-z 0-9 _ . : -</c>.</param>
    /// <param name="label">An optional label of at most 128 characters; left out when <c>null</c>.</param>
    /// <param name="metadata">An optional object of up to 20 keys (1 to 40 of a-z, 0-9 and _), each a text of at most 500 characters, a number or a boolean; left out when <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ValidationError">A reference or label out of its bounds; nothing is sent.</exception>
    /// <exception cref="BadRequestError">The image's URL breaks the API's URL rules (<c>image_url_refused</c>); nothing is sent.</exception>
    public async Task<JsonObject> EnrollAsync(string collectionId, Image image, string reference, string? label = null, JsonObject? metadata = null, CancellationToken cancellationToken = default)
    {
        LocalChecks.CollectionEnroll(image, reference, label, metadata);
        // Only a URL's rules are checked here; every other fault of the picture is the
        // server's 422 image_unreadable.
        if (image.Url is { } url)
        {
            LocalChecks.Url(image.Id, url);
        }
        var images = new[] { image };
        var (wires, _) = await _client.PlaceImagesAsync(images, cancellationToken).ConfigureAwait(false);
        var body = new JsonObject { ["image"] = wires![0]!.DeepClone(), ["reference"] = reference };
        if (label is not null)
        {
            body["label"] = label;
        }
        if (metadata is not null)
        {
            body["metadata"] = metadata.DeepClone();
        }
        return await ObjectAsync(HttpMethod.Post, Path(collectionId) + "/items", body, DecisionNodeClient.RetryPolicy.Idempotent, cancellationToken, NewKey()).ConfigureAwait(false);
    }

    /// <summary>
    /// A page of a collection's items (<c>GET /v1/collections/{id}/items</c>), returned as sent;
    /// each argument sent only when given. <paramref name="reference"/> finds the items of one
    /// reference and <paramref name="referencePrefix"/> those whose reference starts with it.
    /// </summary>
    /// <exception cref="BadRequestError">An offset below 0, a limit outside 1 to 100, both filters, or a filter not 1 to 64 of <c>A-Z a-z 0-9 _ . : -</c>; nothing is sent.</exception>
    public Task<JsonObject> ListItemsAsync(string collectionId, int? offset = null, int? limit = null, string? reference = null, string? referencePrefix = null, CancellationToken cancellationToken = default)
    {
        LocalChecks.ListPage(offset, limit);
        LocalChecks.ItemFilters(reference, referencePrefix);
        var query = new List<string>();
        if (offset is { } o)
        {
            query.Add("offset=" + o.ToString(CultureInfo.InvariantCulture));
        }
        if (limit is { } l)
        {
            query.Add("limit=" + l.ToString(CultureInfo.InvariantCulture));
        }
        if (reference is not null)
        {
            query.Add("reference=" + Uri.EscapeDataString(reference));
        }
        if (referencePrefix is not null)
        {
            query.Add("reference_prefix=" + Uri.EscapeDataString(referencePrefix));
        }
        var path = Path(collectionId) + "/items" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return ObjectAsync(HttpMethod.Get, path, null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);
    }

    /// <summary>
    /// Changes an item (<c>PATCH /v1/collections/{id}/items/{itemId}</c>), sending only the fields
    /// <paramref name="update"/> gives, and returns the item as sent. It carries an
    /// <c>Idempotency-Key</c> and retries as <see cref="EnrollAsync"/> does. A document's
    /// reference, label, metadata, pin and pin rank apply at once, never re-embedded or billed;
    /// a new title makes a new version (the API answers <c>202</c>).
    /// </summary>
    /// <exception cref="ValidationError">Nothing to change, or a reference, label, metadata, pin rank or title out of its bounds; nothing is sent.</exception>
    public async Task<JsonObject> UpdateItemAsync(string collectionId, string itemId, CollectionItemUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Label is not null && update.ClearLabel)
        {
            throw new ArgumentException("give Label or ClearLabel, not both", nameof(update));
        }
        var labelGiven = update.Label is not null || update.ClearLabel;
        LocalChecks.CollectionUpdate(update.Reference, labelGiven, update.Label, update.Metadata, update.Pinned, update.PinRank, update.Title);
        var body = new JsonObject();
        if (update.Reference is not null)
        {
            body["reference"] = update.Reference;
        }
        if (labelGiven)
        {
            body["label"] = update.Label;
        }
        if (update.Metadata is not null)
        {
            body["metadata"] = update.Metadata.DeepClone();
        }
        if (update.Pinned is { } pinned)
        {
            body["pinned"] = pinned;
        }
        if (update.PinRank is { } pinRank)
        {
            body["pin_rank"] = pinRank;
        }
        if (update.Title is not null)
        {
            body["title"] = update.Title;
        }
        var path = ItemPath(collectionId, itemId);
        return await ObjectAsync(HttpMethod.Patch, path, body, DecisionNodeClient.RetryPolicy.Idempotent, cancellationToken, NewKey()).ConfigureAwait(false);
    }

    /// <summary>Deletes one item (<c>DELETE /v1/collections/{id}/items/{itemId}</c>): a document's text and chunks go at once.</summary>
    public Task<JsonObject> DeleteItemAsync(string collectionId, string itemId, CancellationToken cancellationToken = default) =>
        ObjectAsync(HttpMethod.Delete, ItemPath(collectionId, itemId), null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);

    private static string Path(string collectionId)
    {
        ArgumentNullException.ThrowIfNull(collectionId);
        return "/v1/collections/" + Uri.EscapeDataString(collectionId);
    }

    private static string ItemPath(string collectionId, string itemId)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        return Path(collectionId) + "/items/" + Uri.EscapeDataString(itemId);
    }

    /// <summary>A new idempotency key: a version 4 UUID.</summary>
    private static string NewKey() => Guid.NewGuid().ToString("D");

    private async Task<JsonObject> ObjectAsync(HttpMethod method, string path, JsonObject? body, DecisionNodeClient.RetryPolicy policy, CancellationToken ct, string? idempotencyKey = null, TimeSpan? timeout = null, DecisionNodeClient.AttemptPlan? plan = null)
    {
        var (parsed, result) = await _client.SendJsonAsync(
            method,
            path,
            body is null ? null : JsonValues.ToUtf8(body),
            body is null ? null : DecisionNodeClient.JsonContentType,
            policy,
            ct,
            plan ?? (idempotencyKey is null && timeout is null ? null : new DecisionNodeClient.AttemptPlan(timeout, true, idempotencyKey))).ConfigureAwait(false);
        return parsed as JsonObject ?? throw new ServerError(new ErrorDetails
        {
            Status = result.Status,
            RequestId = result.Header("x-request-id"),
            Message = $"the response of {method.Method} {path} is not a JSON object",
            Body = parsed,
        });
    }
}

/// <summary>
/// What <see cref="CollectionsClient.UpdateItemAsync"/> changes; a field left <c>null</c> is not sent.
/// </summary>
public sealed class CollectionItemUpdate
{
    /// <summary>A new reference, 1 to 64 of <c>A-Z a-z 0-9 _ . : -</c>.</summary>
    public string? Reference { get; init; }

    /// <summary>A new label of 1 to 128 characters. To remove the label, set <see cref="ClearLabel"/> instead.</summary>
    public string? Label { get; init; }

    /// <summary>Removes the item's label (sent as <c>"label": null</c>).</summary>
    public bool ClearLabel { get; init; }

    /// <summary>The item's whole new metadata, replacing the old; an empty object removes it all.</summary>
    public JsonObject? Metadata { get; init; }

    /// <summary>A document's pin: <c>true</c> always gives it to a request that names its knowledge base.</summary>
    public bool? Pinned { get; init; }

    /// <summary>A pinned document's rank, 0 to 1,000: the order of pins, lowest first.</summary>
    public int? PinRank { get; init; }

    /// <summary>A document's new title, 1 to 200 characters once trimmed; it makes a new version.</summary>
    public string? Title { get; init; }
}
