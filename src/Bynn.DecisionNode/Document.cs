using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// The content of one document in a knowledge base (a collection of type <c>text</c>): text
/// held in memory, a local file, a file's bytes or a stream, or a knowledge upload made before.
/// </summary>
/// <remarks>
/// <para>
/// Text (<see cref="FromText"/>) is sent inline, whatever its size. A text or Markdown file
/// (<c>text/plain</c>, <c>text/markdown</c>) under the client's inline threshold that is UTF-8
/// with no NUL is sent inline too, its title by default the file name without its extension.
/// Every other file (PDF, DOCX, PPTX, XLSX, HTML, PNG, JPEG, TIFF, WebP, and larger or non-UTF-8
/// text) is uploaded with <c>POST /v1/uploads</c> (the knowledge purpose) and named by its
/// <c>upload_id</c>. The server converts a file to text; once the text is ready the original is
/// deleted.
/// </para>
/// <para>
/// A file is read when the call is made, never before; a stream is read to its end once, into
/// memory, before anything is sent. Nothing about a document is cached.
/// </para>
/// </remarks>
public sealed class Document
{
    /// <summary>The <c>markdown</c> format: chunked by its headings. The API's default.</summary>
    public const string Markdown = "markdown";

    /// <summary>The <c>text</c> format: plain text, chunked by its paragraphs.</summary>
    public const string PlainText = "text";

    /// <summary>The largest file a document may be: 52,428,800 bytes (50 MB).</summary>
    public const long MaxFileBytes = 52_428_800;

    /// <summary>
    /// The content types a document file may be, in the API's order: text, Markdown, PDF, DOCX,
    /// PPTX, XLSX, HTML, PNG, JPEG, TIFF and WebP.
    /// </summary>
    public static IReadOnlyList<string> ContentTypes { get; } = new[]
    {
        "text/plain",
        "text/markdown",
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "text/html",
        "image/png",
        "image/jpeg",
        "image/tiff",
        "image/webp",
    };

    /// <summary>The content type each file name extension stands for, compared without case.</summary>
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".webp"] = "image/webp",
    };

    private Document(string? text, string? format, string? path, ReadOnlyMemory<byte>? data, Stream? stream, string? uploadId, string? contentType, string? filename)
    {
        Text = text;
        Format = format;
        Path = path;
        Data = data;
        Stream = stream;
        UploadId = uploadId;
        ContentType = string.IsNullOrEmpty(contentType) ? null : contentType;
        Filename = string.IsNullOrEmpty(filename) ? (path is null ? null : System.IO.Path.GetFileName(path)) : filename;
    }

    /// <summary>The text of a document made with <see cref="FromText"/>; <c>null</c> otherwise.</summary>
    public string? Text { get; }

    /// <summary>The format given with <see cref="FromText"/> (<c>markdown</c> or <c>text</c>); <c>null</c> when none was given.</summary>
    public string? Format { get; }

    /// <summary>The local file of a document made with <see cref="FromFile"/>.</summary>
    public string? Path { get; }

    /// <summary>The bytes of a document made with <see cref="FromBytes(byte[], string?, string?)"/>.</summary>
    public ReadOnlyMemory<byte>? Data { get; }

    /// <summary>The upload a document made with <see cref="FromUploadId"/> names.</summary>
    public string? UploadId { get; }

    /// <summary>The content type given; <c>null</c> when it is taken from the file name or the first bytes.</summary>
    public string? ContentType { get; }

    /// <summary>The file name: the one given, else a path's base name; <c>null</c> for bytes and streams given none.</summary>
    public string? Filename { get; }

    internal Stream? Stream { get; }

    internal bool IsText => Text is not null;

    /// <summary>
    /// Text held in memory, sent inline whatever its size (the API takes 512 KiB of normalised
    /// text). A title must be given with it.
    /// </summary>
    /// <param name="text">The text, Markdown by default.</param>
    /// <param name="format"><c>markdown</c> (<see cref="Markdown"/>) or <c>text</c> (<see cref="PlainText"/>); left out when <c>null</c>, so the API's default, Markdown, applies.</param>
    public static Document FromText(string text, string? format = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Document(text, format, null, null, null, null, null, null);
    }

    /// <summary>A local file, read when the call is made.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="contentType">One of <see cref="ContentTypes"/>; taken from the file name's extension, else from the first bytes, when <c>null</c>.</param>
    /// <param name="filename">The name sent with the upload; the path's base name when <c>null</c>.</param>
    public static Document FromFile(string path, string? contentType = null, string? filename = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new Document(null, null, path, null, null, null, contentType, filename);
    }

    /// <summary>A file's bytes, which are not copied: do not change them while the call runs.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="contentType">One of <see cref="ContentTypes"/>; taken from <paramref name="filename"/>'s extension, else from the first bytes, when <c>null</c>.</param>
    /// <param name="filename">The file's name, optional. A DOCX, PPTX, XLSX, text or Markdown file needs it or a content type.</param>
    public static Document FromBytes(byte[] data, string? contentType = null, string? filename = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new Document(null, null, null, data, null, null, contentType, filename);
    }

    /// <summary>A file's bytes, which are not copied: do not change them while the call runs.</summary>
    public static Document FromBytes(ReadOnlyMemory<byte> data, string? contentType = null, string? filename = null) =>
        new(null, null, null, data, null, null, contentType, filename);

    /// <summary>
    /// A readable stream, read to its end once, into memory, when the call is made (the upload
    /// needs its size and checksums first). Reading stops past <see cref="MaxFileBytes"/>. The
    /// stream is not disposed.
    /// </summary>
    /// <param name="stream">The stream, read from its current position.</param>
    /// <param name="contentType">One of <see cref="ContentTypes"/>; taken from <paramref name="filename"/>'s extension, else from the first bytes, when <c>null</c>.</param>
    /// <param name="filename">The file's name, optional.</param>
    public static Document FromStream(Stream stream, string? contentType = null, string? filename = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new Document(null, null, null, null, stream, null, contentType, filename);
    }

    /// <summary>
    /// A knowledge upload made before (<see cref="DecisionNodeClient.UploadDocumentsAsync"/>),
    /// sent as given. It must be named within its hour, by an item of the same workspace.
    /// </summary>
    public static Document FromUploadId(string uploadId)
    {
        ArgumentNullException.ThrowIfNull(uploadId);
        return new Document(null, null, null, null, null, uploadId, null, null);
    }

    /// <summary>
    /// The content type a file name stands for, by its extension compared without case, or
    /// <c>null</c> when the extension is not a document type's.
    /// </summary>
    public static string? ContentTypeOf(string? filename)
    {
        var extension = System.IO.Path.GetExtension(filename);
        return !string.IsNullOrEmpty(extension) && Extensions.TryGetValue(extension, out var type) ? type : null;
    }

    /// <summary>
    /// The content type the first bytes show: a PDF, PNG, JPEG, TIFF or WebP; <c>null</c> for
    /// anything else. DOCX, PPTX, XLSX (ZIP containers) and text cannot be told from their bytes.
    /// </summary>
    public static string? SniffContentType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }
        if (data.StartsWith((ReadOnlySpan<byte>)[0x49, 0x49, 0x2A, 0x00]) || data.StartsWith((ReadOnlySpan<byte>)[0x4D, 0x4D, 0x00, 0x2A]))
        {
            return "image/tiff";
        }
        var image = ImageHeader.Sniff(data);
        return image is ImageHeader.Png or ImageHeader.Jpeg or ImageHeader.WebP ? image : null;
    }

    /// <summary>
    /// The file's bytes, content type and name, read now; the faults the presign would answer,
    /// raised before anything is sent: a type that is not a document type, an empty file, a file
    /// over <see cref="MaxFileBytes"/>. A type that cannot be told is an <see cref="ArgumentException"/>.
    /// </summary>
    /// <param name="index">The file's place in the presign, for the messages.</param>
    /// <param name="ct">Cancels the read.</param>
    internal async Task<DocumentFile> ReadFileAsync(int index, CancellationToken ct)
    {
        if (Text is not null)
        {
            // Uploaded as a file only by UploadDocumentsAsync: Markdown or plain text by its format.
            var bytes = Encoding.UTF8.GetBytes(Text);
            var type = Format == PlainText ? "text/plain" : "text/markdown";
            return Checked(index, bytes, type, Filename);
        }
        byte[] data;
        if (Path is not null)
        {
            try
            {
                var length = new FileInfo(Path).Length;
                if (length > MaxFileBytes)
                {
                    throw TooLarge(index, length);
                }
                data = await File.ReadAllBytesAsync(Path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new ArgumentException($"cannot read the document {Path}: {ex.Message}", nameof(Path), ex);
            }
        }
        else if (Stream is not null)
        {
            data = await ReadStreamAsync(index, Stream, ct).ConfigureAwait(false);
        }
        else
        {
            data = Data!.Value.ToArray();
        }
        var contentType = ContentType ?? ContentTypeOf(Filename) ?? SniffContentType(data);
        return Checked(index, data, contentType, Filename);
    }

    /// <summary>Reads a stream to its end, stopping past <see cref="MaxFileBytes"/>.</summary>
    private static async Task<byte[]> ReadStreamAsync(int index, Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxFileBytes)
            {
                throw TooLarge(index, buffer.Length);
            }
        }
        return buffer.ToArray();
    }

    private static DocumentFile Checked(int index, byte[] data, string? contentType, string? filename)
    {
        // The presign's order: the content type, then the size.
        if (contentType is null)
        {
            throw new ArgumentException(
                "the document's type cannot be told from its name or its first bytes: give contentType or filename", "document");
        }
        if (!ContentTypes.Contains(contentType, StringComparer.Ordinal))
        {
            throw LocalChecks.BadRequest($"files[{index}].content_type: must be one of {string.Join(", ", ContentTypes)}, got {contentType}.", "api_usage_error");
        }
        if (data.Length == 0)
        {
            throw LocalChecks.BadRequest($"files[{index}].byte_size: must be an integer from 1 to {MaxFileBytes}.", "api_usage_error");
        }
        if (data.Length > MaxFileBytes)
        {
            throw TooLarge(index, data.Length);
        }
        return new DocumentFile(data, contentType, filename);
    }

    private static BadRequestError TooLarge(int index, long size) =>
        LocalChecks.BadRequest($"files[{index}].byte_size: {size} is over the limit of {MaxFileBytes} bytes.", "api_usage_error");
}

/// <summary>A document's file, read: its bytes, its content type and its name.</summary>
internal sealed class DocumentFile
{
    internal DocumentFile(byte[] data, string contentType, string? filename)
    {
        Data = data;
        ContentType = contentType;
        Filename = filename;
    }

    public byte[] Data { get; }

    public string ContentType { get; }

    public string? Filename { get; }

    /// <summary>A text or Markdown file, which goes inline when it is small and UTF-8.</summary>
    public bool IsText => ContentType is "text/plain" or "text/markdown";

    /// <summary>The file name without its last extension: an inline file's default title.</summary>
    public string? Stem => Filename is null ? null : System.IO.Path.GetFileNameWithoutExtension(Filename);

    /// <summary>The presign's entry for this file, with the knowledge purpose.</summary>
    public JsonObject PresignEntry()
    {
        var entry = new JsonObject
        {
            ["purpose"] = "knowledge",
            ["content_type"] = ContentType,
            ["byte_size"] = Data.Length,
            ["checksum"] = Convert.ToBase64String(MD5.HashData(Data)),
            ["sha256"] = Convert.ToBase64String(SHA256.HashData(Data)),
        };
        if (Filename is not null)
        {
            entry["filename"] = Filename;
        }
        return entry;
    }

    /// <summary>The bytes as text when they are UTF-8 with no NUL; <c>null</c> otherwise.</summary>
    public string? Utf8Text()
    {
        if (Data.AsSpan().Contains((byte)0))
        {
            return null;
        }
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(Data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
