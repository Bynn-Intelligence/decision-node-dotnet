using System.Text.Json.Nodes;

namespace Bynn.DecisionNode;

/// <summary>
/// An image sent beside the state. Every question in the request can see it, and
/// text in the state or the instructions can refer to it by its <see cref="Id"/>.
/// </summary>
/// <remarks>
/// The limits are the API's (at the time of writing: 4 images a request, 10 MB each,
/// <c>https</c> URLs only) and the server enforces them.
/// </remarks>
public sealed class Image
{
    private Image(string id, byte[]? data, string? mediaType, string? url)
    {
        Id = id;
        Data = data;
        MediaType = mediaType;
        Url = url;
    }

    /// <summary>The image's id, unique within a request.</summary>
    public string Id { get; }

    /// <summary>The image bytes, or <c>null</c> for an image given by URL.</summary>
    public byte[]? Data { get; }

    /// <summary><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>, or <c>null</c> for an image given by URL.</summary>
    public string? MediaType { get; }

    /// <summary>The image's URL, or <c>null</c> for an image sent as bytes.</summary>
    public string? Url { get; }

    /// <summary>
    /// Reads a JPEG, PNG or WebP file now, when the image is made. The media type is taken
    /// from the file's first bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The file cannot be read or is not a JPEG, PNG or WebP image.</exception>
    public static Image FromFile(string id, string path)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(path);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException($"image '{id}': cannot read {path}: {ex.Message}", nameof(path), ex);
        }
        var mediaType = SniffMediaType(data)
            ?? throw new ArgumentException($"image '{id}': {path} is not a JPEG, PNG or WebP file", nameof(path));
        return new Image(id, data, mediaType, null);
    }

    /// <summary>
    /// An image from its bytes. Without <paramref name="mediaType"/> (or with an empty one)
    /// the media type is detected from the first bytes, as for a file.
    /// </summary>
    /// <param name="id">The image's id, unique within a request.</param>
    /// <param name="data">The image bytes.</param>
    /// <param name="mediaType"><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>; detected when <c>null</c> or empty.</param>
    /// <exception cref="ArgumentException">No media type was given and the bytes are not a JPEG, PNG or WebP image.</exception>
    public static Image FromBytes(string id, byte[] data, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrEmpty(mediaType))
        {
            mediaType = SniffMediaType(data)
                ?? throw new ArgumentException($"image '{id}': the bytes are not a JPEG, PNG or WebP image; pass the media type", nameof(data));
        }
        return new Image(id, data, mediaType, null);
    }

    /// <summary>An image the server fetches from an <c>https</c> URL.</summary>
    public static Image FromUrl(string id, string url)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(url);
        return new Image(id, null, null, url);
    }

    /// <summary>The media type of JPEG, PNG or WebP bytes, or <c>null</c> for anything else.</summary>
    public static string? SniffMediaType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }
        if (data.Length >= 12
            && data[..4].SequenceEqual("RIFF"u8)
            && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }

    /// <summary>The image's wire form.</summary>
    public JsonObject ToJson()
    {
        if (Url is not null)
        {
            return new JsonObject { ["id"] = Id, ["url"] = Url };
        }
        return new JsonObject
        {
            ["id"] = Id,
            ["media_type"] = MediaType,
            ["data"] = Convert.ToBase64String(Data!),
        };
    }
}
