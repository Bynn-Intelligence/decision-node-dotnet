using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// An image sent beside the state. Every question in the request can see it. The model sees
/// it as "Picture 1", "Picture 2", ... by its place in the request's images, from 1, with its
/// <see cref="Id"/> beside it, so text in the state or the instructions can refer to it by
/// either.
/// </summary>
/// <remarks>
/// <para>
/// Send an image by URL when you can (<see cref="FromUrl"/>): nothing is uploaded, the
/// request stays small, and the server fetches the image while it reads the request.
/// </para>
/// <para>
/// Local bytes (a file, a byte array, a stream or base64 text) are checked on this machine
/// before any request: JPEG, PNG or WebP by their first bytes, at most 10 MB each, at most
/// 40 megapixels read from the image header, and at most 16 images in a request. A request
/// whose local images total under the client's inline threshold
/// (<see cref="DecisionNodeClient.DefaultInlineThreshold"/>, 512 KiB) carries them inline as
/// base64; at or above it they are uploaded once with <c>POST /v1/uploads</c> and named by
/// <c>upload_id</c>, and an image the client uploaded before is named by its
/// <c>upload_id</c> whatever its size. No image is resized or re-encoded.
/// </para>
/// <para>An <see cref="Image"/> is immutable and can be reused across requests and threads.</para>
/// </remarks>
public sealed class Image
{
    private byte[]? _sha256;
    private string? _fault;
    private bool _checked;

    private Image(string id, ReadOnlyMemory<byte>? data, string? declaredMediaType, string? url, string? uploadId)
    {
        Id = id;
        Data = data;
        DeclaredMediaType = string.IsNullOrEmpty(declaredMediaType) ? null : declaredMediaType;
        Url = url;
        UploadId = uploadId;
        if (data is { } bytes)
        {
            DetectedMediaType = ImageHeader.Sniff(bytes.Span);
        }
    }

    /// <summary>
    /// The image's id, unique within a request. The model sees it beside the image's number
    /// ("Picture 1" for the first image), so text can refer to the image by either.
    /// </summary>
    public string Id { get; }

    /// <summary>The image bytes, or <c>null</c> for an image given by URL or by upload id.</summary>
    public ReadOnlyMemory<byte>? Data { get; }

    /// <summary>
    /// <c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>: the type given when the image
    /// was made, else the type detected from the bytes. <c>null</c> for a URL, and for bytes
    /// of no known format (the request is then refused before it is sent).
    /// </summary>
    public string? MediaType => DeclaredMediaType ?? DetectedMediaType;

    /// <summary>The image's URL, or <c>null</c> for an image sent as bytes or by upload id.</summary>
    public string? Url { get; }

    /// <summary>The upload this image names, for an image made with <see cref="FromUploadId"/>.</summary>
    public string? UploadId { get; }

    internal string? DeclaredMediaType { get; }

    internal string? DetectedMediaType { get; }

    internal bool IsLocal => Data is not null;

    /// <summary>
    /// An image the server fetches from an <c>https</c> URL of at most 2,048 characters. The
    /// recommended form: nothing is uploaded and the URL is never fetched by this library.
    /// </summary>
    public static Image FromUrl(string id, string url)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(url);
        return new Image(id, null, null, url, null);
    }

    /// <summary>
    /// Reads a JPEG, PNG or WebP file now, when the image is made. The media type is taken
    /// from the file's first bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The file cannot be read.</exception>
    public static Image FromFile(string id, string path)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return new Image(id, File.ReadAllBytes(path), null, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException($"image '{id}': cannot read {path}: {ex.Message}", nameof(path), ex);
        }
    }

    /// <summary>Reads a JPEG, PNG or WebP file now, without blocking a thread.</summary>
    /// <exception cref="ArgumentException">The file cannot be read.</exception>
    public static async Task<Image> FromFileAsync(string id, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return new Image(id, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), null, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException($"image '{id}': cannot read {path}: {ex.Message}", nameof(path), ex);
        }
    }

    /// <summary>
    /// An image from its bytes, which are not copied: do not change them while the image is
    /// in use. Without <paramref name="mediaType"/> (or with an empty one) the media type is
    /// detected from the first bytes; a given one must match them.
    /// </summary>
    /// <param name="id">The image's id, unique within a request.</param>
    /// <param name="data">The image bytes.</param>
    /// <param name="mediaType"><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>; detected when <c>null</c> or empty.</param>
    public static Image FromBytes(string id, byte[] data, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(data);
        return new Image(id, data, mediaType, null, null);
    }

    /// <summary>An image from bytes in memory, which are not copied. As <see cref="FromBytes(string, byte[], string?)"/>.</summary>
    public static Image FromBytes(string id, ReadOnlyMemory<byte> data, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new Image(id, data, mediaType, null, null);
    }

    /// <summary>
    /// An image from base64 text (standard alphabet). A <c>data:</c> URL prefix such as
    /// <c>data:image/png;base64,</c> is accepted and skipped.
    /// </summary>
    /// <exception cref="ArgumentException">The text is not base64.</exception>
    public static Image FromBase64(string id, string base64, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(base64);
        var text = base64.AsSpan().Trim();
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = text.IndexOf(',');
            text = comma < 0 ? ReadOnlySpan<char>.Empty : text[(comma + 1)..];
        }
        var buffer = new byte[(text.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64Chars(text, buffer, out var written))
        {
            throw new ArgumentException($"image '{id}': the text is not base64", nameof(base64));
        }
        return new Image(id, buffer.AsMemory(0, written), mediaType, null, null);
    }

    /// <summary>Reads an image from a stream now, to its end. The stream is not disposed.</summary>
    /// <exception cref="ArgumentException">The stream cannot be read.</exception>
    public static Image FromStream(string id, Stream stream, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return new Image(id, buffer.GetBuffer().AsMemory(0, (int)buffer.Length), mediaType, null, null);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            throw new ArgumentException($"image '{id}': cannot read the stream: {ex.Message}", nameof(stream), ex);
        }
    }

    /// <summary>Reads an image from a stream now, to its end, without blocking a thread. The stream is not disposed.</summary>
    /// <exception cref="ArgumentException">The stream cannot be read.</exception>
    public static async Task<Image> FromStreamAsync(string id, Stream stream, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return new Image(id, buffer.GetBuffer().AsMemory(0, (int)buffer.Length), mediaType, null, null);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            throw new ArgumentException($"image '{id}': cannot read the stream: {ex.Message}", nameof(stream), ex);
        }
    }

    /// <summary>
    /// An image uploaded before, by its <c>upload_id</c> (from <see cref="DecisionNodeClient.UploadAsync"/>),
    /// sent as given. <paramref name="mediaType"/>, when given, must equal the type declared at upload.
    /// </summary>
    public static Image FromUploadId(string id, string uploadId, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(uploadId);
        return new Image(id, null, mediaType, null, uploadId);
    }

    /// <summary>The media type of JPEG, PNG or WebP bytes, or <c>null</c> for anything else.</summary>
    public static string? SniffMediaType(ReadOnlySpan<byte> data) => ImageHeader.Sniff(data);

    /// <summary>
    /// The image's wire form as sent inline: <c>{id, url}</c>, <c>{id, upload_id}</c> or
    /// <c>{id, media_type, data}</c>. A request may send local bytes by <c>upload_id</c> instead.
    /// </summary>
    public JsonObject ToJson()
    {
        if (Url is not null)
        {
            return new JsonObject { ["id"] = Id, ["url"] = Url };
        }
        if (UploadId is not null)
        {
            var o = new JsonObject { ["id"] = Id, ["upload_id"] = UploadId };
            if (DeclaredMediaType is not null)
            {
                o["media_type"] = DeclaredMediaType;
            }
            return o;
        }
        return new JsonObject
        {
            ["id"] = Id,
            ["media_type"] = MediaType,
            ["data"] = Convert.ToBase64String(Data!.Value.Span),
        };
    }

    /// <summary>The SHA-256 of the bytes, computed once.</summary>
    internal byte[] Sha256 => _sha256 ??= SHA256.HashData(Data!.Value.Span);

    /// <summary>The upload cache's key: the bytes' SHA-256 and the media type.</summary>
    internal string CacheKey => Convert.ToBase64String(Sha256) + "|" + MediaType;

    /// <summary>
    /// What is wrong with the image's bytes (format, declared type, header, pixels), or
    /// <c>null</c>. Checked once: the image is immutable.
    /// </summary>
    internal string? Fault()
    {
        if (!_checked)
        {
            _fault = FindFault();
            _checked = true;
        }
        return _fault;
    }

    private string? FindFault()
    {
        if (Data is not { } data)
        {
            return null;
        }
        if (DetectedMediaType is null)
        {
            return $"Image {Id} is not a JPEG, PNG or WebP image.";
        }
        if (DeclaredMediaType is not null && DeclaredMediaType != DetectedMediaType)
        {
            return $"Image {Id} could not be decoded as {DeclaredMediaType}.";
        }
        if (ImageHeader.Dimensions(data.Span, DetectedMediaType) is not { } size)
        {
            return $"Image {Id}: its header cannot be read.";
        }
        if ((long)size.Width * size.Height > LocalChecks.MaxImagePixels)
        {
            return $"Image {Id} is {size.Width}x{size.Height} pixels, over 40 megapixels.";
        }
        return null;
    }
}
