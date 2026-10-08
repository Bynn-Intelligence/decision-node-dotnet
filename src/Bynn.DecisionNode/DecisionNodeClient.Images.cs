using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

public sealed partial class DecisionNodeClient
{
    /// <summary>The <c>error_type</c>s of a request naming an upload that can no longer be used.</summary>
    private static readonly HashSet<string> StaleUpload = new(StringComparer.Ordinal)
    {
        "upload_expired", "upload_consumed", "upload_not_found", "upload_incomplete",
    };

    /// <summary>
    /// The connections to image storage, shared by every client: they carry no key and no
    /// caller header, only the bytes and the headers each presigned URL names. Redirects are
    /// never followed, cookies never kept.
    /// </summary>
    private static readonly Lazy<HttpMessageInvoker> SharedStorage = new(() => new HttpMessageInvoker(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
        },
        disposeHandler: true));

    /// <summary>Uploads in progress by cache key, so concurrent requests share one.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Where storage <c>PUT</c>s go. Tests replace it.</summary>
    internal HttpMessageInvoker Storage { get; set; } = SharedStorage.Value;

    /// <summary>
    /// Uploads images with <c>POST /v1/uploads</c> and one <c>PUT</c> each, whatever their
    /// size, and returns each image's <c>upload_id</c> in order. An image this client uploaded
    /// before, whose upload is still usable, is not uploaded again; an image made with
    /// <see cref="Image.FromUploadId"/> returns its own id. Pass the ids to
    /// <see cref="Image.FromUploadId"/> to name the images in later requests, by any key of the
    /// workspace, within the hour.
    /// </summary>
    /// <exception cref="ArgumentException">An image is given by URL: a URL is never uploaded.</exception>
    /// <exception cref="BadRequestError">More than 16 images, or an image that breaks a fixed rule of the API; nothing is sent.</exception>
    public async Task<IReadOnlyList<string>> UploadAsync(IEnumerable<Image> images, CancellationToken cancellationToken = default)
    {
        var list = ImageList(images, nameof(images));
        if (list.Count > LocalChecks.MaxUploadFiles)
        {
            // One presign takes 1 to 16 files: its own 400, raised before anything is sent.
            throw LocalChecks.BadRequest($"files: 1 to {LocalChecks.MaxUploadFiles} files, got {list.Count}.", "api_usage_error");
        }
        foreach (var image in list)
        {
            if (image.Url is not null)
            {
                throw new ArgumentException($"image '{image.Id}' is given by URL; a URL is sent as it is and never uploaded", nameof(images));
            }
            LocalChecks.One(image, Held(null));
        }
        var ids = new string?[list.Count];
        var todo = new List<int>();
        var now = UtcNow();
        for (var i = 0; i < list.Count; i++)
        {
            ids[i] = list[i].UploadId ?? _uploads.Get(list[i].CacheKey, now);
            if (ids[i] is null)
            {
                todo.Add(i);
            }
        }
        // The API takes up to 16 files per presign.
        for (var start = 0; start < todo.Count; start += LocalChecks.MaxUploadFiles)
        {
            var chunk = todo.Skip(start).Take(LocalChecks.MaxUploadFiles).ToList();
            var uploaded = await UploadImagesAsync(chunk.Select(i => list[i]).ToList(), cancellationToken).ConfigureAwait(false);
            for (var k = 0; k < chunk.Count; k++)
            {
                ids[chunk[k]] = uploaded[k];
            }
        }
        return ids.Select(id => id!).ToList();
    }

    /// <summary>
    /// Sends a decision body whose images are placed now: URLs and upload ids as given, local
    /// bytes inline under the threshold, else uploaded. A stale reference to an upload this
    /// client placed is uploaded again and the request sent once more (not a retry).
    /// </summary>
    private async Task<(JsonNode? Body, Attempt Result)> SendWithImagesAsync(string path, JsonObject body, IReadOnlyList<Image> images, CancellationToken ct)
    {
        for (var round = 0; ; round++)
        {
            var (wires, placed) = await PlaceImagesAsync(images, ct).ConfigureAwait(false);
            if (wires is not null)
            {
                SetImages(body, wires);
            }
            try
            {
                return await SendJsonAsync(HttpMethod.Post, path, JsonValues.ToUtf8(body), JsonContentType, RetryPolicy.Default, ct).ConfigureAwait(false);
            }
            catch (BadRequestError error) when (round == 0 && placed.Count > 0 && error.ErrorType is { } type && StaleUpload.Contains(type))
            {
                _uploads.Drop(placed);
            }
        }
    }

    /// <summary>Sets a body's <c>images</c>, keeping its <c>videos</c> after them.</summary>
    internal static void SetImages(JsonObject body, JsonArray wires)
    {
        body.Remove("videos", out var videos);
        body["images"] = wires;
        if (videos is not null)
        {
            body["videos"] = videos;
        }
    }

    /// <summary>
    /// The images' wire forms, and the upload ids this client placed in them. Local bytes
    /// are counted together: under the inline threshold the ones not in the cache go inline,
    /// at or above it they are uploaded in one presign; a cached image goes by its upload id.
    /// </summary>
    internal async Task<(JsonArray? Wires, List<string> Placed)> PlaceImagesAsync(IReadOnlyList<Image> images, CancellationToken ct)
    {
        var placed = new List<string>();
        if (images.Count == 0)
        {
            return (null, placed);
        }
        var wires = new JsonObject[images.Count];
        var local = new List<int>();
        long total = 0;
        var now = UtcNow();
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            if (!image.IsLocal)
            {
                wires[i] = image.ToJson();
                continue;
            }
            total += image.Data!.Value.Length;
            if (_uploads.Get(image.CacheKey, now) is { } cached)
            {
                wires[i] = new JsonObject { ["id"] = image.Id, ["upload_id"] = cached };
                placed.Add(cached);
            }
            else
            {
                local.Add(i);
            }
        }
        if (local.Count > 0 && total >= InlineThreshold)
        {
            var ids = await UploadSharedAsync(local.Select(i => images[i]).ToList(), ct).ConfigureAwait(false);
            for (var k = 0; k < local.Count; k++)
            {
                wires[local[k]] = new JsonObject { ["id"] = images[local[k]].Id, ["upload_id"] = ids[k] };
                placed.Add(ids[k]);
            }
        }
        else
        {
            foreach (var i in local)
            {
                wires[i] = images[i].ToJson();
            }
        }
        return (new JsonArray(wires.Select(w => (JsonNode?)w).ToArray()), placed);
    }

    /// <summary>
    /// Uploads images, sharing uploads already in flight for the same bytes: concurrent
    /// requests with one image (a <c>DecideManyAsync</c> over one document) upload it once.
    /// Returns the upload id of each image, in order.
    /// </summary>
    private async Task<string[]> UploadSharedAsync(IReadOnlyList<Image> images, CancellationToken ct)
    {
        var ids = new string[images.Count];
        var waits = new List<(int Index, Task<string> Upload)>();
        var mine = new List<int>();
        var sources = new Dictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
        for (var i = 0; i < images.Count; i++)
        {
            var key = images[i].CacheKey;
            if (sources.TryGetValue(key, out var own))
            {
                waits.Add((i, own.Task));
                continue;
            }
            var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = _inFlight.GetOrAdd(key, source.Task);
            if (running != source.Task)
            {
                waits.Add((i, running));
                continue;
            }
            sources[key] = source;
            mine.Add(i);
        }
        if (mine.Count > 0)
        {
            try
            {
                var uploaded = await UploadImagesAsync(mine.Select(i => images[i]).ToList(), ct).ConfigureAwait(false);
                for (var k = 0; k < mine.Count; k++)
                {
                    ids[mine[k]] = uploaded[k];
                    sources[images[mine[k]].CacheKey].TrySetResult(uploaded[k]);
                }
            }
            catch (Exception ex)
            {
                foreach (var source in sources.Values)
                {
                    if (source.TrySetException(ex))
                    {
                        _ = source.Task.Exception; // observed: the caller gets the error itself
                    }
                }
                throw;
            }
            finally
            {
                foreach (var (key, source) in sources)
                {
                    _inFlight.TryRemove(new KeyValuePair<string, Task<string>>(key, source.Task));
                }
            }
        }
        foreach (var (index, upload) in waits)
        {
            ids[index] = await upload.WaitAsync(ct).ConfigureAwait(false);
        }
        return ids;
    }

    /// <summary>
    /// One <c>POST /v1/uploads</c> for the distinct images, one <c>PUT</c> each in parallel,
    /// then the cache. Returns the upload id of each image, in order.
    /// </summary>
    private async Task<string[]> UploadImagesAsync(IReadOnlyList<Image> images, CancellationToken ct)
    {
        var distinct = new List<Image>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            if (!index.ContainsKey(image.CacheKey))
            {
                index[image.CacheKey] = distinct.Count;
                distinct.Add(image);
            }
        }
        var (uploads, urlsExpire) = await PresignAsync(distinct, ct).ConfigureAwait(false);
        await Task.WhenAll(distinct.Select(async (image, i) =>
        {
            try
            {
                await PutAsync((JsonObject)uploads[i]!, image, ct).ConfigureAwait(false);
            }
            catch (DecisionNodeError error) when (error.Status == 403 && error.ErrorType == "upload_failed" && UtcNow() >= urlsExpire)
            {
                // The URL's minutes passed before its PUT arrived: presign this image again,
                // once. A 403 inside them is a refusal and is raised as it is.
                var (again, _) = await PresignAsync(new[] { image }, ct).ConfigureAwait(false);
                uploads[i] = again[0]!.DeepClone();
                await PutAsync((JsonObject)uploads[i]!, image, ct).ConfigureAwait(false);
            }
        })).ConfigureAwait(false);
        var ids = new string[distinct.Count];
        for (var i = 0; i < distinct.Count; i++)
        {
            var upload = (JsonObject)uploads[i]!;
            ids[i] = JsonValues.AsString(upload["upload_id"])!;
            if (DateTimeOffset.TryParse(JsonValues.AsString(upload["expires_at"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires))
            {
                _uploads.Put(distinct[i].CacheKey, ids[i], expires);
            }
        }
        return images.Select(image => ids[index[image.CacheKey]]).ToArray();
    }

    /// <summary>
    /// One <c>POST /v1/uploads</c> for <paramref name="images"/>: one upload per file, and
    /// when the URLs stop being usable, by the client's clock (<c>expires_in</c>, 300 s).
    /// </summary>
    private async Task<(JsonArray Uploads, DateTimeOffset UrlsExpire)> PresignAsync(IReadOnlyList<Image> images, CancellationToken ct)
    {
        var files = new JsonArray();
        foreach (var image in images)
        {
            files.Add(new JsonObject
            {
                ["content_type"] = image.MediaType,
                ["byte_size"] = image.Data!.Value.Length,
                ["checksum"] = Convert.ToBase64String(MD5.HashData(image.Data.Value.Span)),
                ["sha256"] = Convert.ToBase64String(image.Sha256),
            });
        }
        var request = JsonValues.ToUtf8(new JsonObject { ["files"] = files });
        var (body, result) = await SendJsonAsync(HttpMethod.Post, "/v1/uploads", request, JsonContentType, RetryPolicy.Default, ct).ConfigureAwait(false);
        if ((body as JsonObject)?["uploads"] is not JsonArray uploads
            || uploads.Count != images.Count
            || uploads.Any(u => u is not JsonObject o || JsonValues.AsString(o["upload_id"]) is null || JsonValues.AsString(o["url"]) is null))
        {
            throw new ServerError(new ErrorDetails
            {
                Status = result.Status,
                RequestId = result.Header("x-request-id"),
                Message = "the response of POST /v1/uploads does not hold one upload with an upload_id and a url per file",
                Body = body,
            });
        }
        var lifetime = JsonValues.TryDouble(body!["expires_in"], out var seconds) && seconds > 0 ? seconds : 300;
        return (uploads, UtcNow().AddSeconds(lifetime));
    }

    /// <summary>
    /// The <c>PUT</c> of one image to its presigned URL: exactly the headers the presign
    /// returned and the bytes; never the key or the client's headers; never a redirect.
    /// </summary>
    private async Task PutAsync(JsonObject upload, Image image, CancellationToken ct)
    {
        var url = JsonValues.AsString(upload["url"])!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackName(uri.Host))))
        {
            throw new DecisionNodeError(new ErrorDetails
            {
                Message = "the storage URL of the upload is not https; the image is not sent",
            });
        }
        var headers = upload["headers"] as JsonObject ?? new JsonObject();
        for (var attempt = 0; ; attempt++)
        {
            int status;
            string text;
            Func<string, string?> header;
            using (var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new ReadOnlyMemoryContent(image.Data!.Value) })
            {
                foreach (var (name, value) in headers)
                {
                    var v = JsonValues.AsString(value) ?? value?.ToJsonString() ?? string.Empty;
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // set from the bytes
                    }
                    if (!request.Headers.TryAddWithoutValidation(name, v))
                    {
                        request.Content.Headers.Remove(name);
                        request.Content.Headers.TryAddWithoutValidation(name, v);
                    }
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(UploadTimeout);
                try
                {
                    using var response = await Storage.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    status = (int)response.StatusCode;
                    text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    var collected = CollectHeaders(response);
                    header = name => collected.TryGetValue(name, out var h) ? h : null;
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException && !ct.IsCancellationRequested)
                {
                    if (attempt < MaxRetries)
                    {
                        await Delay(Backoff(attempt), ct).ConfigureAwait(false);
                        continue;
                    }
                    var details = new ErrorDetails { Message = "the upload to storage failed: " + ex.Message, Inner = ex };
                    throw ex is OperationCanceledException ? new TimeoutError(details) : new ConnectionError(details);
                }
            }
            if (status is >= 200 and <= 299 || (status == 412 && attempt > 0))
            {
                // A 412 on a retry: an earlier attempt stored the object, which is written once.
                return;
            }
            if (status is >= 500 and <= 599 && attempt < MaxRetries)
            {
                await Delay(RetryWait(header) ?? Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }
            var code = Regex.Match(text, "<Code>([^<]*)</Code>");
            throw new DecisionNodeError(new ErrorDetails
            {
                Status = status,
                ErrorType = "upload_failed",
                Message = $"the upload of image '{image.Id}' to storage failed with {status.ToString(CultureInfo.InvariantCulture)}",
                Reason = code.Success ? code.Groups[1].Value : null,
            });
        }
    }
}

/// <summary>
/// The client's uploads by content: the SHA-256 of the bytes and the media type, to the
/// upload id and when it stops being usable. In memory only, at most 256 entries, least
/// recently used out first; an entry is used until 5 minutes before its upload expires.
/// </summary>
internal sealed class UploadCache
{
    internal const int Capacity = 256;
    internal static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _byKey.Count;
            }
        }
    }

    internal string? Get(string key, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_byKey.TryGetValue(key, out var node))
            {
                return null;
            }
            if (now >= node.Value.ExpiresAt - Margin)
            {
                _order.Remove(node);
                _byKey.Remove(key);
                return null;
            }
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.UploadId;
        }
    }

    internal void Put(string key, string uploadId, DateTimeOffset expiresAt)
    {
        lock (_lock)
        {
            if (_byKey.TryGetValue(key, out var old))
            {
                _order.Remove(old);
            }
            var node = _order.AddFirst(new Entry(key, uploadId, expiresAt));
            _byKey[key] = node;
            while (_byKey.Count > Capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _byKey.Remove(last.Value.Key);
            }
        }
    }

    internal void Drop(IEnumerable<string> uploadIds)
    {
        var ids = new HashSet<string>(uploadIds, StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var node in _byKey.Values.Where(n => ids.Contains(n.Value.UploadId)).ToList())
            {
                _order.Remove(node);
                _byKey.Remove(node.Value.Key);
            }
        }
    }

    private sealed record Entry(string Key, string UploadId, DateTimeOffset ExpiresAt);
}
