using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Knowledge (spec 1.4): text collections, documents, uploads, waiting, search, the booster and its reading.</summary>
public class KnowledgeTests
{
    private const string Col = "col_3f6c1a9e0b2d4c7f8a1e5d9b2c4f6a80";
    private const string ItemId = "itm_9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d";
    private const string Queued = """{"id":"itm_9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d","status":"queued"}""";
    private const string Pdf = "application/pdf";

    private static void Same(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual?.ToJsonString());

    private static string Presign(Recorded r)
    {
        var uploads = new JsonArray();
        var i = 0;
        foreach (var f in (JsonArray)r.Json!["files"]!)
        {
            uploads.Add(new JsonObject
            {
                ["upload_id"] = $"upl_{r.Uri.Query.Length}{i}",
                ["url"] = $"https://storage.example.test/k{i}?X-Amz-Signature=abc",
                ["method"] = "PUT",
                ["headers"] = new JsonObject { ["Content-Type"] = (string)f!["content_type"]!, ["If-None-Match"] = "*" },
                ["expires_at"] = "2099-01-01T00:00:00Z",
                ["purpose"] = "knowledge",
            });
            i++;
        }
        return new JsonObject { ["uploads"] = uploads, ["expires_in"] = 300 }.ToJsonString();
    }

    private static (DecisionNodeClient Client, StubHandler Api, StubHandler Storage) Make(Func<Recorded, int, HttpResponseMessage>? items = null, int? maxRetries = null, TimeSpan? timeout = null)
    {
        var api = new StubHandler((r, n) => r.Uri.AbsolutePath == "/v1/uploads" ? Json(201, Presign(r)) : (items ?? ((_, _) => Json(202, Queued)))(r, n));
        var storage = new StubHandler((_, _) => Json(200, ""));
        var dn = Client(api, maxRetries: maxRetries, timeout: timeout);
        dn.Storage = new HttpMessageInvoker(storage);
        dn.Delay = (_, _) => Task.CompletedTask;
        return (dn, api, storage);
    }

    private static byte[] PdfBytes(int size)
    {
        var data = new byte[size];
        "%PDF-1.7\n"u8.ToArray().CopyTo(data, 0);
        return data;
    }

    private static async Task<(T Error, StubHandler Api)> Refused<T>(Func<DecisionNodeClient, Task> call)
        where T : DecisionNodeError
    {
        var (dn, api, _) = Make();
        using var _ = dn;
        var error = await Assert.ThrowsAsync<T>(() => call(dn));
        Assert.Null(error.Status);
        Assert.Empty(api.Requests);
        return (error, api);
    }

    [Fact]
    public void TheSpecVersionIsOnePointFour()
    {
        Assert.Equal("1.4", DecisionNodeClient.SpecVersion);
        Assert.Equal("0.4.1", DecisionNodeClient.Version);
    }

    [Fact]
    public async Task ATextCollectionIsCreatedWithItsScopeKey()
    {
        var (dn, api, _) = Make((_, _) => Json(201, """{"id":"col_1","type":"text"}"""));
        using var _ = dn;
        await dn.Collections.CreateAsync("Claims policy", "text", "mission");
        await dn.Collections.CreateAsync("Faces");
        var sent = api.Requests.ToList();
        Assert.Equal("""{"type":"text","name":"Claims policy","scope_key":"mission"}""", sent[0].Body);
        Assert.Equal("""{"type":"face","name":"Faces"}""", sent[1].Body);
        Assert.True(Guid.TryParse(sent[0].Headers["Idempotency-Key"], out var key) && key != Guid.Empty);

        var (error, _) = await Refused<ValidationError>(c => c.Collections.CreateAsync("Claims", "text", "Mission"));
        Same("""[{"type":"string_pattern_mismatch","loc":["body","scope_key"],"msg":"String should match pattern '^[a-z0-9_]{1,40}$'","input":"Mission"}]""", error.Entries);
    }

    [Fact]
    public async Task MarkdownIsSentInlineWithEveryFieldAndAKeyPerCall()
    {
        var (dn, api, storage) = Make();
        using var _ = dn;
        var metadata = new JsonObject { ["mission"] = "M-42" };
        await dn.Collections.AddDocumentAsync(Col, Document.FromText("# Receipts\nReject any claim over 500 SEK without a receipt."), "Claims policy",
            reference: "policy-2026", pinned: true, pinRank: 3, label: "Policy", metadata: metadata);
        await dn.Collections.AddDocumentAsync(Col, Document.FromText("plain", Document.PlainText), "Note", wait: true);
        var sent = api.Requests.ToList();
        Assert.Equal($"/v1/collections/{Col}/items", sent[0].Uri.PathAndQuery);
        Same("""{"title":"Claims policy","text":"# Receipts\nReject any claim over 500 SEK without a receipt.","reference":"policy-2026","pinned":true,"pin_rank":3,"label":"Policy","metadata":{"mission":"M-42"}}""", sent[0].Json);
        Assert.Equal($"/v1/collections/{Col}/items?wait=true", sent[1].Uri.PathAndQuery);
        Same("""{"title":"Note","text":"plain","format":"text"}""", sent[1].Json);
        Assert.NotEqual(sent[0].Headers["Idempotency-Key"], sent[1].Headers["Idempotency-Key"]);
        Assert.Empty(storage.Requests);
    }

    [Fact]
    public async Task TheKeyIsTheSameOnEveryAttempt()
    {
        var (dn, api, _) = Make((_, n) => n < 2 ? Json(503, """{"detail":{"error_type":"api_error","message":"busy"}}""") : Json(202, Queued));
        using var _ = dn;
        await dn.Collections.AddDocumentAsync(Col, Document.FromText("x"), "T");
        var keys = api.Requests.Select(r => r.Headers["Idempotency-Key"]).Distinct().ToList();
        Assert.Equal(3, api.Requests.Count);
        Assert.Single(keys);
    }

    [Fact]
    public async Task ASmallMarkdownFileGoesInlineTitledByItsStem()
    {
        var (dn, api, storage) = Make();
        using var _ = dn;
        await dn.Collections.AddDocumentAsync(Col, Document.FromBytes(Encoding.UTF8.GetBytes("# Rules\nBe kind."), filename: "house-rules.v2.md"));
        Same("""{"title":"house-rules.v2","text":"# Rules\nBe kind.","format":"markdown"}""", Assert.Single(api.Requests).Json);
        Assert.Empty(storage.Requests);
    }

    [Fact]
    public async Task APdfIsUploadedWithTheKnowledgePurposeThenNamedByUploadId()
    {
        var (dn, api, storage) = Make();
        using var _ = dn;
        var pdf = PdfBytes(2000);
        using var stream = new MemoryStream(pdf);
        await dn.Collections.AddDocumentAsync(Col, Document.FromStream(stream, filename: "claims-policy-2026.pdf"), reference: "policy-2026");
        var sent = api.Requests.ToList();
        Assert.Equal(new[] { "/v1/uploads", $"/v1/collections/{Col}/items" }, sent.Select(r => r.Uri.AbsolutePath));
        Same($$"""
            {"files":[{"purpose":"knowledge","content_type":"application/pdf","byte_size":2000,
              "checksum":"{{Convert.ToBase64String(MD5.HashData(pdf))}}","sha256":"{{Convert.ToBase64String(SHA256.HashData(pdf))}}",
              "filename":"claims-policy-2026.pdf"}]}
            """, sent[0].Json);
        Same("""{"upload_id":"upl_00","reference":"policy-2026"}""", sent[1].Json);
        var put = Assert.Single(storage.Requests);
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.False(put.Headers.ContainsKey("Authorization"));
        Assert.Equal("*", put.Headers["If-None-Match"]);
    }

    [Fact]
    public async Task TheContentTypeComesFromTheNameThenTheBytes()
    {
        Assert.Equal("text/markdown", Document.ContentTypeOf("A.MARKDOWN"));
        Assert.Equal("image/tiff", Document.ContentTypeOf("scan.TIF"));
        Assert.Null(Document.ContentTypeOf("notes"));
        Assert.Equal(Pdf, Document.SniffContentType(PdfBytes(10)));
        Assert.Equal("image/tiff", Document.SniffContentType(new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0 }));
        Assert.Null(Document.SniffContentType("PK\u0003\u0004"u8));

        var (dn, api, _) = Make();
        using var _ = dn;
        await dn.Collections.AddDocumentAsync(Col, Document.FromBytes(new byte[] { 0x50, 0x4B, 3, 4, 9 }, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"), "Handbook");
        var file = api.Requests.First().Json!["files"]![0]!.AsObject();
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", (string?)file["content_type"]);
        Assert.False(file.ContainsKey("filename"));
        Same("""{"upload_id":"upl_00","title":"Handbook"}""", api.Requests.Last().Json);
    }

    [Fact]
    public async Task TheInlineThresholdDecidesForTextFiles()
    {
        var (dn, api, storage) = Make();
        using var _ = dn;
        var under = Encoding.UTF8.GetBytes(new string('a', DecisionNodeClient.DefaultInlineThreshold - 1));
        var at = Encoding.UTF8.GetBytes(new string('a', DecisionNodeClient.DefaultInlineThreshold));
        await dn.Collections.AddDocumentAsync(Col, Document.FromBytes(under, filename: "under.txt"));
        await dn.Collections.AddDocumentAsync(Col, Document.FromBytes(at, filename: "at.txt"));
        await dn.Collections.AddDocumentAsync(Col, Document.FromBytes(new byte[] { 0x61, 0xFF, 0x62 }, "text/plain", "latin1.txt"));
        await dn.Collections.AddDocumentAsync(Col, Document.FromBytes(new byte[] { 0x61, 0x00, 0x62 }, "text/plain", "nul.txt"));
        var items = api.Requests.Where(r => r.Uri.AbsolutePath != "/v1/uploads").Select(r => r.Json!.AsObject()).ToList();
        Assert.Equal("text", (string?)items[0]["format"]);
        Assert.Equal("under", (string?)items[0]["title"]);
        Assert.All(items.Skip(1), i => Assert.True(i.ContainsKey("upload_id") && !i.ContainsKey("format") && !i.ContainsKey("title")));
        Assert.Equal(3, storage.Requests.Count);
    }

    [Fact]
    public async Task AnUploadIdIsSentAsGivenAndAStaleOneIsRaisedAtOnce()
    {
        var (dn, api, _) = Make((_, _) => Json(400, """{"detail":{"error_type":"upload_expired","message":"Upload upl_x has expired."}}"""));
        using var _ = dn;
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.Collections.AddDocumentAsync(Col, Document.FromUploadId("upl_x"), "T"));
        Assert.Equal("upload_expired", error.ErrorType);
        Same("""{"upload_id":"upl_x","title":"T"}""", Assert.Single(api.Requests).Json);
    }

    [Fact]
    public async Task AStaleUploadThisCallPlacedIsUploadedAgainOnceWithANewKey()
    {
        var calls = 0;
        var (dn, api, storage) = Make((_, _) => ++calls == 1
            ? Json(400, """{"detail":{"error_type":"upload_consumed","message":"used"}}""")
            : Json(202, Queued));
        using var _ = dn;
        await dn.Collections.AddDocumentAsync(Col, Document.FromBytes(PdfBytes(100), filename: "a.pdf"));
        var sent = api.Requests.ToList();
        Assert.Equal(new[] { "/v1/uploads", "/items", "/v1/uploads", "/items" }, sent.Select(r => r.Uri.AbsolutePath.EndsWith("/items") ? "/items" : r.Uri.AbsolutePath));
        Assert.NotEqual(sent[1].Headers["Idempotency-Key"], sent[3].Headers["Idempotency-Key"]);
        Assert.Equal(2, storage.Requests.Count);

        var (again, againApi, _) = Make((_, _) => Json(400, """{"detail":{"error_type":"upload_not_found","message":"gone"}}"""));
        using var __ = again;
        var error = await Assert.ThrowsAsync<BadRequestError>(() => again.Collections.AddDocumentAsync(Col, Document.FromBytes(PdfBytes(100), filename: "a.pdf")));
        Assert.Equal("upload_not_found", error.ErrorType);
        Assert.Equal(4, againApi.Requests.Count);
    }

    [Fact]
    public async Task APurposeMismatchIsNeverUploadedAgain()
    {
        var (dn, api, _) = Make((_, _) => Json(400, """{"detail":{"error_type":"upload_purpose_mismatch","message":"upload upl_00 is an image upload"}}"""));
        using var _ = dn;
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.Collections.AddDocumentAsync(Col, Document.FromBytes(PdfBytes(100), filename: "a.pdf")));
        Assert.Equal("upload_purpose_mismatch", error.ErrorType);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task TheItemFieldsAreCheckedLocallyInTheApisOrder()
    {
        var (missing, _) = await Refused<ValidationError>(c => c.Collections.AddDocumentAsync(Col, Document.FromText("x")));
        Same("""[{"type":"missing","loc":["body","title"],"msg":"Field required","input":null}]""", missing.Entries);

        var (many, _) = await Refused<ValidationError>(c => c.Collections.AddDocumentAsync(Col, Document.FromText("x", "html"), new string('t', 201),
            reference: "a b", pinRank: 1001, label: "", metadata: new JsonObject { ["Bad"] = 1 }));
        Same("""
            [{"type":"string_too_long","loc":["body","title"],"msg":"A title is 1 to 200 characters","input":"tttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttt..."},
             {"type":"literal_error","loc":["body","format"],"msg":"Input should be 'markdown' or 'text'","input":"html"},
             {"type":"string_pattern_mismatch","loc":["body","reference"],"msg":"String should match pattern '^[A-Za-z0-9_.:-]{1,64}$'","input":"a b","ctx":{"pattern":"^[A-Za-z0-9_.:-]{1,64}$"}},
             {"type":"int_range","loc":["body","pin_rank"],"msg":"Input should be an integer from 0 to 1000","input":1001},
             {"type":"string_too_short","loc":["body","label"],"msg":"String should have at least 1 character","input":"","ctx":{"min_length":1}},
             {"type":"value_error","loc":["body","metadata"],"msg":"A metadata key is 1 to 40 characters of a-z, 0-9 and _: \"Bad\".","input":null}]
            """, many.Entries);

        var (blank, _) = await Refused<ValidationError>(c => c.Collections.AddDocumentAsync(Col, Document.FromText("x"), "   "));
        Assert.Equal("string_too_long", (string?)blank.Entries![0]!["type"]);

        var (empty, _) = await Refused<ValidationError>(c => c.Collections.AddDocumentAsync(Col, Document.FromText(""), "T"));
        Same("""[{"type":"string_too_short","loc":["body","text"],"msg":"The text is empty","input":""}]""", empty.Entries);

        var (noName, _) = await Refused<ValidationError>(c => c.Collections.AddDocumentAsync(Col, Document.FromBytes("abc"u8.ToArray(), "text/plain")));
        Same("""[{"type":"missing","loc":["body","title"],"msg":"Field required","input":null}]""", noName.Entries);
    }

    [Fact]
    public async Task AFileIsCheckedAsThePresignChecksIt()
    {
        var (type, _) = await Refused<BadRequestError>(c => c.Collections.AddDocumentAsync(Col, Document.FromBytes(new byte[] { 1, 2, 3 }, "image/gif")));
        Assert.Equal("files[0].content_type: must be one of text/plain, text/markdown, application/pdf, application/vnd.openxmlformats-officedocument.wordprocessingml.document, application/vnd.openxmlformats-officedocument.presentationml.presentation, application/vnd.openxmlformats-officedocument.spreadsheetml.sheet, text/html, image/png, image/jpeg, image/tiff, image/webp, got image/gif.", type.Message);
        Assert.Equal("api_usage_error", type.ErrorType);

        var (dn, api, _) = Make();
        using var _ = dn;
        await Assert.ThrowsAsync<ArgumentException>(() => dn.Collections.AddDocumentAsync(Col, Document.FromBytes(new byte[] { 1, 2, 3 }), "Untold"));
        Assert.Empty(api.Requests);

        var (empty, _) = await Refused<BadRequestError>(c => c.Collections.AddDocumentAsync(Col, Document.FromBytes(Array.Empty<byte>(), Pdf)));
        Assert.Equal("files[0].byte_size: must be an integer from 1 to 52428800.", empty.Message);

        var (large, _) = await Refused<BadRequestError>(c => c.Collections.AddDocumentAsync(Col, Document.FromStream(new MemoryStream(PdfBytes(52_428_801)), Pdf)));
        Assert.Equal("files[0].byte_size: 52428801 is over the limit of 52428800 bytes.", large.Message);
    }

    [Fact]
    public async Task ReplaceAndUpdateItem()
    {
        var (dn, api, _) = Make((_, _) => Ok(Queued));
        using var _ = dn;
        await dn.Collections.ReplaceDocumentAsync(Col, ItemId, Document.FromText("new"), "Policy", wait: true);
        await dn.Collections.UpdateItemAsync(Col, ItemId, new CollectionItemUpdate { Pinned = true, PinRank = 0, Title = "Renamed" });
        var sent = api.Requests.ToList();
        Assert.Equal((HttpMethod.Put, $"/v1/collections/{Col}/items/{ItemId}?wait=true"), (sent[0].Method, sent[0].Uri.PathAndQuery));
        Same("""{"title":"Policy","text":"new"}""", sent[0].Json);
        Assert.Equal(HttpMethod.Patch, sent[1].Method);
        Same("""{"pinned":true,"pin_rank":0,"title":"Renamed"}""", sent[1].Json);
        Assert.True(sent[1].Headers.ContainsKey("Idempotency-Key"));

        var (error, _) = await Refused<ValidationError>(c => c.Collections.UpdateItemAsync(Col, ItemId, new CollectionItemUpdate { PinRank = -1 }));
        Same("""[{"type":"int_range","loc":["body","pin_rank"],"msg":"Input should be an integer from 0 to 1000","input":-1}]""", error.Entries);
    }

    [Fact]
    public async Task TheReads()
    {
        var (dn, api, _) = Make((_, _) => Ok("""{"deleted":3}"""));
        using var _ = dn;
        await dn.Collections.GetItemAsync(Col, ItemId);
        await dn.Collections.GetItemAsync(Col, ItemId, includeText: true);
        await dn.Collections.ListChunksAsync(Col, ItemId);
        await dn.Collections.ListChunksAsync(Col, ItemId, offset: 20, limit: 10);
        await dn.Collections.DeleteFailedAsync(Col);
        Assert.Equal(new[]
        {
            $"GET /v1/collections/{Col}/items/{ItemId}",
            $"GET /v1/collections/{Col}/items/{ItemId}?include=text",
            $"GET /v1/collections/{Col}/items/{ItemId}/chunks",
            $"GET /v1/collections/{Col}/items/{ItemId}/chunks?offset=20&limit=10",
            $"DELETE /v1/collections/{Col}/items?status=failed",
        }, api.Requests.Select(r => $"{r.Method} {r.Uri.PathAndQuery}"));
        await Refused<BadRequestError>(c => c.Collections.ListChunksAsync(Col, ItemId, limit: 101));
        await dn.Collections.ListItemsAsync(Col, limit: 10, q: "claims policy", sort: "title");
        Assert.Equal($"/v1/collections/{Col}/items?limit=10&q=claims%20policy&sort=title", api.Requests.Last().Uri.PathAndQuery);
        var (empty, _) = await Refused<BadRequestError>(c => c.Collections.ListItemsAsync(Col, q: ""));
        Assert.Equal("Query parameter q must be 1 to 200 characters.", empty.Message);
        var (sort, _) = await Refused<BadRequestError>(c => c.Collections.ListItemsAsync(Col, sort: "gr\u00f6\u00dfer-als-sechzehn"));
        Assert.Equal("Query parameter sort must be newest, oldest or title, got gr\\u{f6}\\u{df}er-als-sechz....", sort.Message);
    }

    [Fact]
    public async Task WaitingForAnItemPollsUntilReadyOrFailed()
    {
        var waits = new List<TimeSpan>();
        var (dn, api, _) = Make((_, n) => Ok(n < 2 ? """{"id":"i","status":"processing"}""" : """{"id":"i","status":"ready"}"""));
        using var _ = dn;
        dn.Delay = (w, _) => { waits.Add(w); return Task.CompletedTask; };
        var item = await dn.Collections.WaitUntilReadyAsync(Col, ItemId, pollInterval: TimeSpan.FromSeconds(2));
        Assert.Equal("ready", (string?)item["status"]);
        Assert.Equal(3, api.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) }, waits);

        var (failing, _, _) = Make((_, _) => Ok("""{"id":"i","status":"failed","error":"chunks_limit_reached"}"""));
        using var __ = failing;
        Assert.Equal("chunks_limit_reached", (string?)(await failing.Collections.WaitUntilReadyAsync(Col, ItemId))["error"]);
    }

    [Fact]
    public async Task WaitingForACollectionReadsItsCounts()
    {
        var (dn, api, _) = Make((_, n) => Ok(n == 0
            ? """{"id":"c","status_counts":{"queued":1,"processing":0,"ready":2,"failed":0}}"""
            : """{"id":"c","status_counts":{"ready":3,"failed":0}}"""));
        using var _ = dn;
        await dn.Collections.WaitUntilReadyAsync(Col);
        Assert.Equal(2, api.Requests.Count);
        Assert.All(api.Requests, r => Assert.Equal($"/v1/collections/{Col}", r.Uri.PathAndQuery));
    }

    [Fact]
    public async Task WaitingGrowsToTenSecondsAndTimesOut()
    {
        var waits = new List<TimeSpan>();
        var (dn, _, _) = Make((_, _) => Ok("""{"id":"i","status":"embedding_later"}"""));
        using var _ = dn;
        dn.Delay = (w, _) => { waits.Add(w); return Task.CompletedTask; };
        await Assert.ThrowsAsync<TimeoutException>(() => dn.Collections.WaitUntilReadyAsync(Col, ItemId, timeout: TimeSpan.FromSeconds(40), pollInterval: TimeSpan.FromSeconds(4)));
        Assert.Equal(new[] { 4.0, 6, 9, 10, 10, 1 }, waits.Select(w => w.TotalSeconds));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dn.Collections.WaitUntilReadyAsync(Col, ItemId, pollInterval: TimeSpan.FromSeconds(0.05)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dn.Collections.WaitUntilReadyAsync(Col, ItemId, timeout: TimeSpan.Zero));
    }

    [Fact]
    public async Task WaitingHonoursALongerRetryAfter()
    {
        var waits = new List<TimeSpan>();
        var (dn, _, _) = Make((_, n) => n switch
        {
            0 => Ok("""{"id":"i","status":"queued"}"""),
            1 => Json(429, """{"detail":{"error_type":"rate_limit_error","message":"slow"}}""", ("Retry-After", "7")),
            2 => Ok("""{"id":"i","status":"processing"}"""),
            _ => Ok("""{"id":"i","status":"ready"}"""),
        });
        using var _ = dn;
        dn.Delay = (w, _) => { waits.Add(w); return Task.CompletedTask; };
        await dn.Collections.WaitUntilReadyAsync(Col, ItemId);
        // The first poll, the read's own retry after the 429, then a poll as long as the 429 asked.
        Assert.Equal(new[] { 1.0, 7.0, 7.0 }, waits.Select(w => w.TotalSeconds));
    }

    private const string Reading = """
        {"collection":"col_3f6c","collection_name":"Claims policy","corpus":{"version":41},
         "query":{"source":"auto","tokens":212},"budget":{"mode":"auto","tokens":840,"entry":"short","cause":null},
         "scope":{"key":"mission","value":"M-42"},"warnings":["budget_floor"],
         "passages":[{"n":1,"pinned":true,"item":"itm_1","reference":"policy-2026","title":"Claims policy 2026","heading":"Receipts",
                      "chunk":3,"byte_start":1840,"byte_end":2310,"tokens":96,"score":null,"lexical":false,"rendered":true},
                     {"n":2,"pinned":false,"item":"itm_2","reference":null,"title":null,"heading":null,
                      "chunk":0,"byte_start":0,"byte_end":12,"tokens":5,"score":0.8123,"lexical":true,"rendered":false,"text":"Photos count"}],
         "omitted":{"over_budget":1},"pending_items":0,"search":{"plan":"exact","candidates":50,"fallback":false},"truncated":false}
        """;

    [Fact]
    public async Task TheBoosterIsBuiltAndItsReadingTyped()
    {
        Same("""{"name":"knowledge","options":{"collection":"col_1"}}""", Booster.Knowledge("col_1").ToJson());
        Same("""
            {"name":"knowledge","require":true,"options":{"collection":"col_1","query":"q","scope":"M-42","match":{"region":"SE"},
             "filter":{"key":"k","op":"eq","value":1},"top_k":0,"max_tokens":"auto","pinned":false,"min_score":0.5,"include_text":true}}
            """, Booster.Knowledge("col_1", "q", "M-42", new JsonObject { ["region"] = "SE" }, new { key = "k", op = "eq", value = 1 }, 0, "auto", false, 0.5, true, require: true).ToJson());

        var body = """{"model":"m","answers":{"q":{"type":"truth","truth":0.9}},"usage":{"input_tokens":9,"output_tokens":0},"evidence":{"knowledge":{"version":"knowledge-v1","status":"ok","latency_ms":141,"billed_calls":1,"results":["""
            + Reading + "]}}}";
        var (dn, api, _) = Make((_, _) => Ok(body));
        using var _ = dn;
        var d = await dn.DecideAsync("s", OneTruth(), boosters: new[] { Booster.Knowledge(Col, scope: "M-42", require: true) });
        Assert.Equal(dn.Timeout, DecisionNodeClient.DefaultTimeout);
        var reading = Assert.IsType<KnowledgeReading>(Assert.Single(d.Evidence!["knowledge"].Results));
        Assert.Equal("col_3f6c", reading.Collection);
        Assert.Equal(41, reading.Corpus!.Version);
        Assert.Equal(("auto", 212L), (reading.Query!.Source, reading.Query.Tokens!.Value));
        Assert.Equal(new KnowledgeScope("mission", "M-42"), reading.Scope);
        Assert.Equal(new[] { "budget_floor" }, reading.Warnings);
        Assert.Equal(2, reading.Passages.Count);
        Assert.Null(reading.Passages[0].Score);
        Assert.Equal(0.8123, reading.Passages[1].Score);
        Assert.Equal("Photos count", reading.Passages[1].Text);
        Assert.Equal(1, (int)reading.Omitted!["over_budget"]!);
        Assert.False(reading.Truncated);
        Assert.Null(reading.Image);
    }

    [Fact]
    public void AMalformedReadingOrPassageIsGeneric()
    {
        foreach (var bad in new[]
        {
            """{"passages":[]}""",
            """{"collection":"c","passages":{}}""",
            """{"collection":"c","passages":[{"n":1,"pinned":"yes","item":"i","chunk":0,"byte_start":0,"byte_end":1}]}""",
            """{"collection":"c","passages":[{"n":1,"pinned":true,"item":"i","chunk":0,"byte_start":0}]}""",
        })
        {
            Assert.IsType<UnknownReading>(DecisionNode.Reading.Parse("knowledge", JsonNode.Parse(bad)));
        }
        var minimal = Assert.IsType<KnowledgeReading>(DecisionNode.Reading.Parse("knowledge", JsonNode.Parse("""{"collection":"c","passages":[]}""")));
        Assert.Empty(minimal.Warnings);
        Assert.Null(minimal.Truncated);
    }

    [Fact]
    public async Task SearchSendsWhatIsGivenAndReadsTheReading()
    {
        var (dn, api, _) = Make((_, _) => Ok(Reading));
        using var _ = dn;
        var reading = await dn.Collections.SearchAsync(Col, "receipts", topK: 3);
        await dn.Collections.SearchAsync(Col, "q", "M-42", new { region = "SE" }, new JsonObject { ["and"] = new JsonArray() }, 0, false, 512);
        var sent = api.Requests.ToList();
        Assert.Equal($"/v1/collections/{Col}/search", sent[0].Uri.PathAndQuery);
        Assert.Equal("""{"query":"receipts","top_k":3}""", sent[0].Body);
        Same("""{"query":"q","scope":"M-42","match":{"region":"SE"},"filter":{"and":[]},"top_k":0,"pinned":false,"max_tokens":512}""", sent[1].Json);
        Assert.Equal(2, reading.Passages.Count);
    }

    [Fact]
    public async Task AnIngestionDelayIsRaisedAtOnceAndOtherKnowledgeOverloadsRetried()
    {
        var delayed = """{"detail":{"error_type":"booster_unavailable","message":"Ingestion is delayed.","details":{"reason":"ingestion_delayed"}}}""";
        var (dn, api, _) = Make((_, _) => Json(529, delayed, ("Retry-After", "60")));
        using var _ = dn;
        var error = await Assert.ThrowsAsync<OverloadedError>(() => dn.DecideAsync("s", OneTruth(), boosters: new[] { Booster.Knowledge(Col, require: true) }));
        Assert.Equal("ingestion_delayed", (string?)error.Details!["reason"]);
        Assert.Single(api.Requests);

        var plain = """{"detail":{"error_type":"booster_unavailable","message":"No document is ready yet."}}""";
        var (again, againApi, _) = Make((_, n) => n == 0 ? Json(529, plain, ("Retry-After", "5")) : Ok());
        using var __ = again;
        await again.DecideAsync("s", OneTruth(), boosters: new[] { Booster.Knowledge(Col, require: true) });
        Assert.Equal(2, againApi.Requests.Count);
    }

    [Fact]
    public async Task UploadDocumentsReturnsAnIdEach()
    {
        var (dn, api, storage) = Make();
        using var _ = dn;
        var ids = await dn.UploadDocumentsAsync(new[]
        {
            Document.FromBytes(PdfBytes(10), filename: "a.pdf"),
            Document.FromUploadId("upl_given"),
            Document.FromText("# Note", Document.Markdown),
        });
        Assert.Equal(new[] { "upl_00", "upl_given", "upl_01" }, ids);
        var files = (JsonArray)Assert.Single(api.Requests).Json!["files"]!;
        Assert.Equal(new[] { Pdf, "text/markdown" }, files.Select(f => (string)f!["content_type"]!));
        Assert.Equal(2, storage.Requests.Count);

        var (error, _) = await Refused<BadRequestError>(c => c.UploadDocumentsAsync(Enumerable.Range(0, 17).Select(_ => Document.FromText("x"))));
        Assert.Equal("files: 1 to 16 files, got 17.", error.Message);
    }

    [Fact]
    public void AWaitCallIsAllowedTwentySecondsUnlessATimeoutWasSet()
    {
        var (dn, _, _) = Make();
        using var _ = dn;
        Assert.Equal(TimeSpan.FromSeconds(20), dn.WaitCallTimeout);
        var (set, _, _) = Make(timeout: TimeSpan.FromSeconds(3));
        using var __ = set;
        Assert.Equal(TimeSpan.FromSeconds(3), set.WaitCallTimeout);
    }
}
