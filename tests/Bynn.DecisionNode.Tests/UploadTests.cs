using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Images inline under the threshold, uploaded at or above it, and the upload cache.</summary>
public class UploadTests
{
    private static byte[] Padded(int size)
    {
        var data = new byte[size];
        Fixtures.Read("rgb-7x3.png").CopyTo(data, 0);
        return data;
    }

    private static string Presign(Recorded r, string? url = null, string expiresAt = "2099-01-01T00:00:00Z")
    {
        var files = (JsonArray)r.Json!["files"]!;
        var uploads = new JsonArray();
        var i = 0;
        foreach (var f in files)
        {
            uploads.Add(new JsonObject
            {
                ["upload_id"] = "upl_" + i.ToString("x32"),
                ["url"] = url ?? $"https://storage.example.test/obj{i}?X-Amz-Signature=abc",
                ["method"] = "PUT",
                ["headers"] = new JsonObject
                {
                    ["Content-Type"] = (string)f!["content_type"]!,
                    ["Content-MD5"] = (string)f["checksum"]!,
                    ["If-None-Match"] = "*",
                    ["x-amz-checksum-sha256"] = (string)f["sha256"]!,
                },
                ["expires_at"] = expiresAt,
            });
            i++;
        }
        return new JsonObject { ["uploads"] = uploads, ["expires_in"] = 300 }.ToJsonString();
    }

    private static (DecisionNodeClient Client, StubHandler Api, StubHandler Storage) Make(
        Func<Recorded, int, HttpResponseMessage>? api = null,
        Func<Recorded, int, HttpResponseMessage>? storage = null,
        int? maxRetries = null)
    {
        var apiStub = new StubHandler(api ?? ((r, _) => r.Uri.AbsolutePath == "/v1/uploads" ? Json(201, Presign(r)) : Ok()));
        var storageStub = new StubHandler(storage ?? ((_, _) => Json(200, "")));
        var dn = Client(apiStub, maxRetries: maxRetries, headers: new Dictionary<string, string> { ["x-custom"] = "1" });
        dn.Storage = new HttpMessageInvoker(storageStub);
        dn.Delay = (_, _) => Task.CompletedTask;
        return (dn, apiStub, storageStub);
    }

    [Fact]
    public async Task UnderTheThresholdImagesGoInline()
    {
        var (dn, api, storage) = Make();
        using var _ = dn;
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("a", Padded(DecisionNodeClient.DefaultInlineThreshold - 1)) });
        var image = (JsonObject)Assert.Single(api.Requests).Json!["images"]![0]!;
        Assert.Equal("image/png", (string?)image["media_type"]);
        Assert.NotNull(image["data"]);
        Assert.Empty(storage.Requests);
    }

    [Fact]
    public async Task AtTheThresholdTheRequestsImagesAreUploadedInOnePresign()
    {
        var (dn, api, storage) = Make();
        using var _ = dn;
        var a = Padded(300_000);
        var b = Padded(300_000);
        b[^1] = 1; // different bytes
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("a", a), Image.FromBytes("b", b) });

        var requests = api.Requests.ToList();
        Assert.Equal(new[] { "/v1/uploads", "/v1/decide" }, requests.Select(r => r.Uri.AbsolutePath));
        var files = (JsonArray)requests[0].Json!["files"]!;
        Assert.Equal(2, files.Count);
        Assert.Equal(Convert.ToBase64String(MD5.HashData(a)), (string?)files[0]!["checksum"]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(a)), (string?)files[0]!["sha256"]);
        Assert.Equal(300_000, (int)files[0]!["byte_size"]!);

        var images = (JsonArray)requests[1].Json!["images"]!;
        Assert.Equal("upl_" + 0.ToString("x32"), (string?)images[0]!["upload_id"]);
        Assert.Equal("upl_" + 1.ToString("x32"), (string?)images[1]!["upload_id"]);
        Assert.Null(images[0]!["data"]);
        Assert.Equal(2, storage.Requests.Count);
    }

    [Fact]
    public async Task SixteenImagesAreUploadedInOnePresignAndUploadSplitsAtSixteen()
    {
        var (dn, api, storage) = Make();
        using var _ = dn;
        Image Distinct(int i)
        {
            var data = Padded(40_000);
            data[^1] = (byte)i;
            return Image.FromBytes("i" + i, data);
        }
        await dn.DecideAsync("s", OneTruth(), Enumerable.Range(0, 16).Select(Distinct));
        var requests = api.Requests.ToList();
        Assert.Equal(new[] { "/v1/uploads", "/v1/decide" }, requests.Select(r => r.Uri.AbsolutePath));
        Assert.Equal(16, ((JsonArray)requests[0].Json!["files"]!).Count);
        Assert.Equal(16, storage.Requests.Count);

        var ids = await dn.UploadAsync(Enumerable.Range(100, 17).Select(Distinct));
        Assert.Equal(17, ids.Count);
        var presigns = api.Requests.Skip(2).Select(r => ((JsonArray)r.Json!["files"]!).Count).ToList();
        Assert.Equal(new[] { 16, 1 }, presigns);
    }

    [Fact]
    public async Task ThePutCarriesExactlyThePresignedHeadersAndNeverTheKey()
    {
        var (dn, _, storage) = Make();
        using var __ = dn;
        var data = Padded(600_000);
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("a", data) });
        var put = Assert.Single(storage.Requests);
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.False(put.Headers.ContainsKey("Authorization"));
        Assert.False(put.Headers.ContainsKey("x-custom"));
        Assert.False(put.Headers.ContainsKey("User-Agent"));
        Assert.Equal("image/png", put.Headers["Content-Type"]);
        Assert.Equal(Convert.ToBase64String(MD5.HashData(data)), put.Headers["Content-MD5"]);
        Assert.Equal("*", put.Headers["If-None-Match"]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(data)), put.Headers["x-amz-checksum-sha256"]);
    }

    [Fact]
    public async Task ACachedImageGoesByItsUploadIdWhateverItsSize()
    {
        var (dn, api, storage) = Make();
        using var _ = dn;
        var big = Image.FromBytes("a", Padded(600_000));
        var ids = await dn.UploadAsync(new[] { Image.FromBytes("small", Fixtures.Read("rgb-7x3.png")) });
        await dn.DecideAsync("s", OneTruth(), new[] { big });
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("again", Padded(600_000)) });
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("small", Fixtures.Read("rgb-7x3.png")) });

        Assert.Equal(2, api.Requests.Count(r => r.Uri.AbsolutePath == "/v1/uploads"));
        Assert.Equal(2, storage.Requests.Count);
        var last = api.Requests.Last().Json!["images"]![0]!;
        Assert.Equal(ids[0], (string?)last["upload_id"]);
    }

    [Fact]
    public async Task ConcurrentRequestsWithOneImageUploadItOnce()
    {
        var apiStub = new StubHandler((r, _) => r.Uri.AbsolutePath == "/v1/uploads" ? Json(201, Presign(r)) : Ok());
        using var dn = Client(apiStub);
        var storage = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(100, ct);
            return Json(200, "");
        });
        dn.Storage = new HttpMessageInvoker(storage);
        var data = Padded(600_000);
        var results = await dn.DecideManyAsync(Enumerable.Range(0, 8).Select(i => new DecideRequest
        {
            State = "page " + i,
            Questions = OneTruth(),
            Images = new[] { Image.FromBytes("doc", data) },
        }));
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, apiStub.Requests.Count(r => r.Uri.AbsolutePath == "/v1/uploads"));
        Assert.Single(storage.Requests);
        Assert.Equal(8, apiStub.Requests.Count(r => r.Uri.AbsolutePath == "/v1/decide"));
    }

    [Fact]
    public async Task AnEntryIsUsedUntilFiveMinutesBeforeItExpires()
    {
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var (dn, api, _) = Make(api: (r, _) => r.Uri.AbsolutePath == "/v1/uploads" ? Json(201, Presign(r, expiresAt: "2026-10-08T13:00:00Z")) : Ok());
        using var __ = dn;
        dn.UtcNow = () => now;
        var image = Image.FromBytes("a", Padded(600_000));
        await dn.DecideAsync("s", OneTruth(), new[] { image });
        now = now.AddMinutes(54);
        await dn.DecideAsync("s", OneTruth(), new[] { image });
        Assert.Equal(1, api.Requests.Count(r => r.Uri.AbsolutePath == "/v1/uploads"));
        now = now.AddMinutes(1);
        await dn.DecideAsync("s", OneTruth(), new[] { image });
        Assert.Equal(2, api.Requests.Count(r => r.Uri.AbsolutePath == "/v1/uploads"));
    }

    [Fact]
    public async Task AStaleReferenceIsUploadedAgainAndSentOnceMore()
    {
        var decides = 0;
        var (dn, api, _) = Make(api: (r, _) =>
        {
            if (r.Uri.AbsolutePath == "/v1/uploads")
            {
                return Json(201, Presign(r));
            }
            return Interlocked.Increment(ref decides) == 1
                ? Json(400, """{"detail":{"error_type":"upload_expired","message":"expired"}}""")
                : Ok();
        });
        using var _ = dn;
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("a", Padded(600_000)) });
        Assert.Equal(new[] { "/v1/uploads", "/v1/decide", "/v1/uploads", "/v1/decide" }, api.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task AnUploadIdTheCallerGaveIsNeverUploadedAgain()
    {
        var (dn, api, _) = Make(api: (_, _) => Json(400, """{"detail":{"error_type":"upload_not_found","message":"gone"}}"""));
        using var __ = dn;
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync("s", OneTruth(), new[] { Image.FromUploadId("a", "upl_" + new string('0', 32)) }));
        Assert.Equal("upload_not_found", error.ErrorType);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task ARetriedPutAnswered412Succeeded()
    {
        var (dn, _, storage) = Make(storage: (_, i) => i == 0 ? Json(503, "<Error><Code>SlowDown</Code></Error>") : Json(412, "<Error><Code>PreconditionFailed</Code></Error>"));
        using var __ = dn;
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("a", Padded(600_000)) });
        Assert.Equal(2, storage.Requests.Count);
    }

    [Fact]
    public async Task AFailedPutIsTheBaseErrorWithTheStorageCode()
    {
        var (dn, api, storage) = Make(storage: (_, _) => Json(400, "<Error><Code>BadDigest</Code></Error>"));
        using var __ = dn;
        var error = await Assert.ThrowsAsync<DecisionNodeError>(() => dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("a", Padded(600_000)) }));
        Assert.Equal(typeof(DecisionNodeError), error.GetType());
        Assert.Equal(400, error.Status);
        Assert.Equal("upload_failed", error.ErrorType);
        Assert.Equal("BadDigest", error.Reason);
        Assert.Single(storage.Requests);
        Assert.Single(api.Requests); // the presign; the decision is never sent
    }

    [Fact]
    public async Task APlainHttpStorageUrlIsRefusedBeforeAnyByte()
    {
        var (dn, _, storage) = Make(api: (r, _) => Json(201, Presign(r, url: "http://storage.example.test/obj?sig")));
        using var __ = dn;
        await Assert.ThrowsAnyAsync<DecisionNodeError>(() => dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("a", Padded(600_000)) }));
        Assert.Empty(storage.Requests);
    }

    [Fact]
    public async Task UrlsPassThroughAndAreNeverUploaded()
    {
        var (dn, api, storage) = Make();
        using var _ = dn;
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromUrl("u", "https://files.example.com/r.jpg") });
        Assert.Equal("""[{"id":"u","url":"https://files.example.com/r.jpg"}]""", Assert.Single(api.Requests).Json!["images"]!.ToJsonString());
        Assert.Empty(storage.Requests);
        await Assert.ThrowsAsync<ArgumentException>(() => dn.UploadAsync(new[] { Image.FromUrl("u", "https://files.example.com/r.jpg") }));
    }

    [Fact]
    public async Task InlineThresholdZeroUploadsEveryLocalImage()
    {
        var apiStub = new StubHandler((r, _) => r.Uri.AbsolutePath == "/v1/uploads" ? Json(201, Presign(r)) : Ok());
        using var dn = new DecisionNodeClient(new DecisionNodeClientOptions
        {
            ApiKey = "dn_test_key",
            BaseUrl = "https://api.example.test",
            HttpClient = new HttpClient(apiStub),
            DelegatingHandlersNeverResendRequests = true,
            InlineThreshold = 0,
        });
        dn.Storage = new HttpMessageInvoker(new StubHandler((_, _) => Json(200, "")));
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("a", Fixtures.Read("rgb-7x3.png")) });
        Assert.Equal(2, apiStub.Requests.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionNodeClient(new DecisionNodeClientOptions { ApiKey = "k", InlineThreshold = -1 }));
    }

    [Fact]
    public void TheCacheKeepsAtMost256EntriesLeastRecentlyUsedOutFirst()
    {
        var cache = new UploadCache();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 300; i++)
        {
            cache.Put("k" + i, "upl_" + i, now.AddHours(1));
            if (i == 200)
            {
                Assert.Equal("upl_0", cache.Get("k0", now)); // k0 becomes the most recent
            }
        }
        Assert.Equal(256, cache.Count);
        Assert.Equal("upl_0", cache.Get("k0", now));
        Assert.Null(cache.Get("k1", now));
        Assert.Equal("upl_299", cache.Get("k299", now));
        cache.Drop(new[] { "upl_299" });
        Assert.Null(cache.Get("k299", now));
    }
}
