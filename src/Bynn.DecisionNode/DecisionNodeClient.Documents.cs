using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

public sealed partial class DecisionNodeClient
{
    /// <summary>
    /// The default time allowed for each attempt of a document's storage <c>PUT</c> (a file is
    /// up to 50 MB) when <see cref="DecisionNodeClientOptions.UploadTimeout"/> is not set.
    /// </summary>
    public static readonly TimeSpan DefaultDocumentUploadTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The default time allowed for each attempt of an item call with <c>wait</c>, which the
    /// server holds up to 10 seconds, when <see cref="DecisionNodeClientOptions.Timeout"/> is not set.
    /// </summary>
    internal static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Uploads 1 to 16 documents for a knowledge base, whatever their type and size, with one
    /// <c>POST /v1/uploads</c> (the knowledge purpose) and one <c>PUT</c> each, and returns each
    /// one's <c>upload_id</c> in order, for <see cref="Document.FromUploadId"/>. A text document
    /// is uploaded as Markdown or plain text by its format; a document made with
    /// <see cref="Document.FromUploadId"/> returns its own id. An upload not named by an item
    /// within its hour expires. Needs a full-scope key.
    /// </summary>
    /// <exception cref="BadRequestError">More than 16 documents (or none), or a file that is empty, over 50 MB, or of a type that is not a document's or cannot be told; nothing is sent.</exception>
    /// <exception cref="PermissionDeniedError">The key is not full-scope (<c>key_scope_denied</c>).</exception>
    public async Task<IReadOnlyList<string>> UploadDocumentsAsync(IEnumerable<Document> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var list = documents.ToList();
        if (list.Count is 0 or > LocalChecks.MaxUploadFiles)
        {
            // One presign takes 1 to 16 files: its own 400, raised before anything is sent.
            throw LocalChecks.BadRequest($"files: 1 to {LocalChecks.MaxUploadFiles} files, got {list.Count}.", "api_usage_error");
        }
        var ids = new string?[list.Count];
        var files = new List<DocumentFile>();
        var places = new List<int>();
        for (var i = 0; i < list.Count; i++)
        {
            var document = list[i] ?? throw new ArgumentNullException(nameof(documents), "a document is null");
            if (document.UploadId is { } given)
            {
                ids[i] = given;
                continue;
            }
            files.Add(await document.ReadFileAsync(files.Count, cancellationToken).ConfigureAwait(false));
            places.Add(i);
        }
        if (files.Count > 0)
        {
            var uploaded = await UploadDocumentFilesAsync(files, cancellationToken).ConfigureAwait(false);
            for (var k = 0; k < places.Count; k++)
            {
                ids[places[k]] = uploaded[k];
            }
        }
        return ids.Select(id => id!).ToList();
    }

    /// <summary>
    /// One <c>POST /v1/uploads</c> for the files, with the knowledge purpose, and one <c>PUT</c>
    /// each in parallel; a <c>PUT</c> refused after its URL's minutes is presigned again once.
    /// Returns each file's <c>upload_id</c>, in order. Nothing is cached.
    /// </summary>
    internal async Task<string[]> UploadDocumentFilesAsync(IReadOnlyList<DocumentFile> files, CancellationToken ct)
    {
        var entries = new JsonArray(files.Select(f => (JsonNode?)f.PresignEntry()).ToArray());
        var (uploads, urlsExpire) = await PresignFilesAsync(entries, ct).ConfigureAwait(false);
        var timeout = _uploadTimeoutSet ? UploadTimeout : DefaultDocumentUploadTimeout;
        await Task.WhenAll(files.Select(async (file, i) =>
        {
            var what = file.Filename is null ? $"document {i + 1}" : $"document '{file.Filename}'";
            try
            {
                await PutAsync((JsonObject)uploads[i]!, file.Data, "document", what, timeout, ct).ConfigureAwait(false);
            }
            catch (DecisionNodeError error) when (error.Status == 403 && error.ErrorType == "upload_failed" && UtcNow() >= urlsExpire)
            {
                // The URL's minutes passed before its PUT arrived: presign this file again, once.
                var (again, _) = await PresignFilesAsync(new JsonArray(file.PresignEntry()), ct).ConfigureAwait(false);
                uploads[i] = again[0]!.DeepClone();
                await PutAsync((JsonObject)uploads[i]!, file.Data, "document", what, timeout, ct).ConfigureAwait(false);
            }
        })).ConfigureAwait(false);
        return uploads.Select(u => JsonValues.AsString(u!["upload_id"])!).ToArray();
    }
}
