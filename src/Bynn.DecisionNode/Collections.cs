using System.Globalization;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// A workspace's collections of faces, and the public collections it may use, which the
/// <c>face_recognition</c> booster searches. Every call returns the API's object exactly as
/// sent. <see cref="CreateAsync"/> and <see cref="EnrollAsync"/> send an <c>Idempotency-Key</c>,
/// a new one per call and the same on every attempt of it, so the API creates, enrolls and bills
/// once however often the call is tried; they are retried as any call, and also on a <c>409</c>
/// <c>idempotency_in_progress</c>. A <c>409</c> <c>idempotency_key_reused</c> is thrown as the
/// base <see cref="DecisionNodeError"/>. The reads and deletes carry no key.
/// </summary>
public sealed class CollectionsClient
{
    private readonly DecisionNodeClient _client;

    internal CollectionsClient(DecisionNodeClient client)
    {
        _client = client;
    }

    /// <summary>Creates a collection (<c>POST /v1/collections</c>).</summary>
    /// <param name="name">The collection's name, 1 to 80 characters.</param>
    /// <param name="type">The kind of thing it holds; <c>face</c>, the one kind there is.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ValidationError">A type other than <c>face</c>, or a name not 1 to 80 characters; nothing is sent.</exception>
    public async Task<JsonObject> CreateAsync(string name, string type = "face", CancellationToken cancellationToken = default)
    {
        LocalChecks.CollectionCreate(name, type);
        var body = new JsonObject { ["type"] = type, ["name"] = name };
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

    /// <summary>Deletes a collection and its items (<c>DELETE /v1/collections/{id}</c>).</summary>
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
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ValidationError">A reference or label out of its bounds; nothing is sent.</exception>
    /// <exception cref="BadRequestError">The image's URL breaks the API's URL rules (<c>image_url_refused</c>); nothing is sent.</exception>
    public async Task<JsonObject> EnrollAsync(string collectionId, Image image, string reference, string? label = null, CancellationToken cancellationToken = default)
    {
        LocalChecks.CollectionEnroll(image, reference, label);
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
        return await ObjectAsync(HttpMethod.Post, Path(collectionId) + "/items", body, DecisionNodeClient.RetryPolicy.Idempotent, cancellationToken, NewKey()).ConfigureAwait(false);
    }

    /// <summary>A page of a collection's items (<c>GET /v1/collections/{id}/items</c>); <paramref name="offset"/> and <paramref name="limit"/> sent only when given.</summary>
    /// <exception cref="BadRequestError">An offset below 0 or a limit outside 1 to 100; nothing is sent.</exception>
    public Task<JsonObject> ListItemsAsync(string collectionId, int? offset = null, int? limit = null, CancellationToken cancellationToken = default)
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
        var path = Path(collectionId) + "/items" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return ObjectAsync(HttpMethod.Get, path, null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);
    }

    /// <summary>Deletes one item (<c>DELETE /v1/collections/{id}/items/{itemId}</c>).</summary>
    public Task<JsonObject> DeleteItemAsync(string collectionId, string itemId, CancellationToken cancellationToken = default) =>
        ObjectAsync(HttpMethod.Delete, Path(collectionId) + "/items/" + Uri.EscapeDataString(itemId), null, DecisionNodeClient.RetryPolicy.Default, cancellationToken);

    private static string Path(string collectionId)
    {
        ArgumentNullException.ThrowIfNull(collectionId);
        return "/v1/collections/" + Uri.EscapeDataString(collectionId);
    }

    /// <summary>A new idempotency key: a version 4 UUID.</summary>
    private static string NewKey() => Guid.NewGuid().ToString("D");

    private async Task<JsonObject> ObjectAsync(HttpMethod method, string path, JsonObject? body, DecisionNodeClient.RetryPolicy policy, CancellationToken ct, string? idempotencyKey = null)
    {
        var (parsed, result) = await _client.SendJsonAsync(
            method,
            path,
            body is null ? null : JsonValues.ToUtf8(body),
            body is null ? null : DecisionNodeClient.JsonContentType,
            policy,
            ct,
            idempotencyKey is null ? null : new DecisionNodeClient.AttemptPlan(null, true, idempotencyKey)).ConfigureAwait(false);
        return parsed as JsonObject ?? throw new ServerError(new ErrorDetails
        {
            Status = result.Status,
            RequestId = result.Header("x-request-id"),
            Message = $"the response of {method.Method} {path} is not a JSON object",
            Body = parsed,
        });
    }
}
