using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// A video sent beside the state, as the frames you sampled, each with its time. Every
/// question in the request sees every video, the images and the state, and text in the state
/// or the instructions can refer to the video by its <see cref="Id"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only frames are sent: no video file, no video URL and no sound. Frames always go inline as
/// base64; they are never uploaded, never cached and never resized or re-encoded, and they do
/// not count toward the client's inline threshold for images. To turn a video file into
/// frames, use the <c>dn</c> command line's <c>--video</c> option.
/// </para>
/// <para>
/// The fixed rules of a video are checked on this machine before anything is sent (at most 4
/// videos, an id unique across images and videos, frame times of at least 0 that increase,
/// the frames' media types, <c>fps</c>, <c>start</c> and <c>end</c>), each raising the
/// <see cref="BadRequestError"/> the API would answer. The frame counts, the frames' bytes and
/// sizes and the token budget are the server's to judge.
/// </para>
/// <para>A <see cref="Video"/> is immutable and can be reused across requests and threads.</para>
/// </remarks>
public sealed class Video
{
    private readonly JsonObject _wire;

    private Video(JsonObject wire)
    {
        _wire = wire;
    }

    /// <summary>The video's id, unique across the request's images and videos; <c>null</c> when a wire form had none.</summary>
    public string? Id => JsonValues.AsString(_wire["id"]);

    /// <summary>
    /// A video from frames you already hold, in time order.
    /// </summary>
    /// <param name="id">The video's id, unique across the request's images and videos.</param>
    /// <param name="frames">The frames, in time order. At least one.</param>
    /// <param name="fps">A cap on the frames read a second, above 0 and at most 4. Left out when <c>null</c>: the server reads up to 2 a second.</param>
    /// <param name="start">The start of the window read, in the frames' own seconds. Left out when <c>null</c> (0).</param>
    /// <param name="end">The end of the window read (not included), after <paramref name="start"/>. Left out when <c>null</c>: every frame from <paramref name="start"/>.</param>
    public static Video Of(string id, IEnumerable<VideoFrame> frames, double? fps = null, double? start = null, double? end = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(frames);
        var list = new JsonArray();
        foreach (var frame in frames)
        {
            ArgumentNullException.ThrowIfNull(frame, nameof(frames));
            list.Add(frame.ToJson());
        }
        var wire = new JsonObject { ["id"] = id };
        if (fps is { } f)
        {
            wire["fps"] = f;
        }
        if (start is { } s)
        {
            wire["start"] = s;
        }
        if (end is { } e)
        {
            wire["end"] = e;
        }
        wire["frames"] = list;
        return new Video(wire);
    }

    /// <summary>
    /// A video in the API's wire form, <c>{"id", "fps"?, "start"?, "end"?, "frames": [{"t", "media_type", "data"}]}</c>,
    /// sent as given (copied) after the same local checks as a video made with <see cref="Of"/>.
    /// </summary>
    public static Video FromJson(JsonObject wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return new Video((JsonObject)wire.DeepClone());
    }

    /// <summary>The video's wire form, a copy.</summary>
    public JsonObject ToJson() => (JsonObject)_wire.DeepClone();

    /// <summary>The wire form itself, for the request body and the checks. Never changed.</summary>
    internal JsonObject Wire => _wire;
}

/// <summary>
/// One frame of a <see cref="Video"/>: its time in seconds and its image (JPEG, PNG or WebP),
/// sent inline as base64.
/// </summary>
public sealed class VideoFrame
{
    private readonly JsonObject _wire;

    private VideoFrame(JsonObject wire)
    {
        _wire = wire;
    }

    /// <summary>
    /// A frame from its image bytes. Without <paramref name="mediaType"/> (or with an empty one)
    /// the media type is detected from the first bytes; bytes of another format are refused
    /// before the request is sent.
    /// </summary>
    /// <param name="t">The frame's time in seconds: at least 0, larger than the frame before it in the same video.</param>
    /// <param name="data">The image bytes.</param>
    /// <param name="mediaType"><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>; detected when <c>null</c> or empty.</param>
    public static VideoFrame FromBytes(double t, byte[] data, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        return FromBytes(t, data.AsMemory(), mediaType);
    }

    /// <summary>A frame from image bytes in memory. As <see cref="FromBytes(double, byte[], string?)"/>.</summary>
    public static VideoFrame FromBytes(double t, ReadOnlyMemory<byte> data, string? mediaType = null)
    {
        var type = string.IsNullOrEmpty(mediaType) ? ImageHeader.Sniff(data.Span) : mediaType;
        return new VideoFrame(Wire(t, type, Convert.ToBase64String(data.Span)));
    }

    /// <summary>
    /// A frame from base64 text, sent exactly as given, with its media type. Base64 text needs
    /// its media type: without one the request is refused before it is sent.
    /// </summary>
    /// <param name="t">The frame's time in seconds: at least 0, larger than the frame before it in the same video.</param>
    /// <param name="base64">The image as base64 text (standard alphabet), sent exactly as given.</param>
    /// <param name="mediaType"><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>.</param>
    public static VideoFrame FromBase64(double t, string base64, string? mediaType)
    {
        ArgumentNullException.ThrowIfNull(base64);
        return new VideoFrame(Wire(t, string.IsNullOrEmpty(mediaType) ? null : mediaType, base64));
    }

    /// <summary>A frame in the API's wire form, <c>{"t", "media_type", "data"}</c>, sent as given (copied).</summary>
    public static VideoFrame FromJson(JsonObject wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return new VideoFrame((JsonObject)wire.DeepClone());
    }

    /// <summary>The frame's wire form, a copy.</summary>
    public JsonObject ToJson() => (JsonObject)_wire.DeepClone();

    private static JsonObject Wire(double t, string? mediaType, string data) => new()
    {
        ["t"] = t,
        ["media_type"] = mediaType,
        ["data"] = data,
    };
}
