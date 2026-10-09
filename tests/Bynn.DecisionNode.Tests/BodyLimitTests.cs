using System.Text;
using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>The API's 25 MiB body limit, checked before sending, and its 413.</summary>
public class BodyLimitTests
{
    private const string TooLarge = "The request body is over 26,214,400 bytes (25 MiB). Send large images as uploads (POST /v1/uploads) and name them by upload_id.";

    /// <summary>The bytes a decide body with one truth question takes besides its state, measured on the wire.</summary>
    private static async Task<int> OverheadAsync()
    {
        var handler = new StubHandler((_, _) => Ok());
        using var dn = Client(handler);
        await dn.DecideAsync("a", OneTruth());
        return Encoding.UTF8.GetByteCount(Assert.Single(handler.Requests).Body!) - 1;
    }

    private static void AssertLocal(RequestTooLargeError e, long size)
    {
        Assert.Null(e.Status);
        Assert.Null(e.RequestId);
        Assert.Equal("request_too_large_error", e.ErrorType);
        Assert.Equal(TooLarge, e.Message);
        Assert.Equal($"the body is {size} bytes; the limit is 26214400", e.Reason);
    }

    [Fact]
    public void TheLimitIs25MiB()
    {
        Assert.Equal(26_214_400, DecisionNodeClient.MaxBodyBytes);
        Assert.True(typeof(RequestTooLargeError).BaseType == typeof(DecisionNodeError));
    }

    [Fact]
    public async Task ABodyOfExactlyTheLimitIsSentAndOneByteMoreIsNot()
    {
        var overhead = await OverheadAsync();
        var handler = new StubHandler((_, _) => Ok());
        using var dn = Client(handler);

        await dn.DecideAsync(new string('a', DecisionNodeClient.MaxBodyBytes - overhead), OneTruth());
        Assert.Equal(DecisionNodeClient.MaxBodyBytes, Encoding.UTF8.GetByteCount(Assert.Single(handler.Requests).Body!));

        var over = new string('a', DecisionNodeClient.MaxBodyBytes - overhead + 1);
        var e = await Assert.ThrowsAsync<RequestTooLargeError>(() => dn.DecideAsync(over, OneTruth()));
        AssertLocal(e, DecisionNodeClient.MaxBodyBytes + 1);
        AssertLocal(Assert.Throws<RequestTooLargeError>(() => dn.CheckRequest(new DecideRequest { State = over, Questions = OneTruth() })), DecisionNodeClient.MaxBodyBytes + 1);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DecideManyFailsWholeAndSendsNothing()
    {
        var overhead = await OverheadAsync();
        var handler = new StubHandler((_, _) => Ok());
        using var dn = Client(handler);
        var requests = new[]
        {
            new DecideRequest { State = "small", Questions = OneTruth() },
            new DecideRequest { State = new string('a', DecisionNodeClient.MaxBodyBytes - overhead + 1), Questions = OneTruth() },
        };
        await Assert.ThrowsAsync<RequestTooLargeError>(() => dn.DecideManyAsync(requests));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EachBatchLineIsCheckedAsItWouldBeSent()
    {
        var handler = new StubHandler((_, _) => Ok());
        using var dn = Client(handler);
        var line = new BatchRequest { CustomId = "big", State = new string('a', DecisionNodeClient.MaxBodyBytes), Questions = OneTruth() };
        var e = await Assert.ThrowsAsync<RequestTooLargeError>(() => dn.Batches.CreateAsync(new[] { line }));
        Assert.Null(e.Status);
        Assert.Throws<RequestTooLargeError>(() => dn.Batches.CheckCreate(new[] { line }));
        Assert.Throws<RequestTooLargeError>(() => dn.Batches.CheckAdd(new[] { line }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ImagesItWouldUploadAreMeasuredByTheirUploadIdAndNothingIsUploaded()
    {
        var overhead = await OverheadAsync();
        var storage = new StubHandler((_, _) => Json(200, ""));
        var api = new StubHandler((r, _) => r.Uri.AbsolutePath == "/v1/uploads" ? Json(201, Presign(r)) : Ok());
        using var dn = Client(api);
        dn.Storage = new HttpMessageInvoker(storage);
        var image = Image.FromBytes("big", Padded(9_900_000));
        // The image part of the body as sent: ,"images":[{"id":"big","upload_id":"upl_<32>"}]
        var imagePart = ",\"images\":[{\"id\":\"big\",\"upload_id\":\"upl_00000000000000000000000000000000\"}]".Length;
        var fits = new string('a', DecisionNodeClient.MaxBodyBytes - overhead - imagePart);

        await Assert.ThrowsAsync<RequestTooLargeError>(() => dn.DecideAsync(fits + "a", OneTruth(), new[] { image }));
        Assert.Empty(api.Requests);
        Assert.Empty(storage.Requests);

        await dn.DecideAsync(fits, OneTruth(), new[] { image });
        var sent = api.Requests.Last();
        Assert.Equal("/v1/decide", sent.Uri.AbsolutePath);
        Assert.Equal(DecisionNodeClient.MaxBodyBytes, Encoding.UTF8.GetByteCount(sent.Body!));
        Assert.Single(storage.Requests);
    }

    [Fact]
    public async Task TwoLargeImagesAreUploadedSoTheBodyStaysUnder()
    {
        var storage = new StubHandler((_, _) => Json(200, ""));
        var api = new StubHandler((r, _) => r.Uri.AbsolutePath == "/v1/uploads" ? Json(201, Presign(r)) : Ok());
        using var dn = Client(api);
        dn.Storage = new HttpMessageInvoker(storage);
        var front = Padded(9_900_000);
        var back = Padded(9_900_000);
        back[^1] = 1;
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("front", front), Image.FromBytes("back", back) });
        Assert.Equal(new[] { "/v1/uploads", "/v1/decide" }, api.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.Equal(2, storage.Requests.Count);
        Assert.True(api.Requests.Last().Body!.Length < 1000);
    }

    [Fact]
    public async Task A413IsRequestTooLargeAndNotRetried()
    {
        var handler = new StubHandler((_, _) => Json(413,
            """{"detail":{"error_type":"request_too_large_error","message":"The request body is over 26,214,400 bytes (25 MiB). Send large images as uploads (POST /v1/uploads) and name them by upload_id."}}""",
            ("x-request-id", "req_413")));
        using var dn = Client(handler);
        var e = await Assert.ThrowsAsync<RequestTooLargeError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal(413, e.Status);
        Assert.Equal("req_413", e.RequestId);
        Assert.Equal("request_too_large_error", e.ErrorType);
        Assert.Equal(TooLarge, e.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DecideRawSendsItsBodyAsGiven()
    {
        var handler = new StubHandler((_, _) => Ok());
        using var dn = Client(handler);
        await dn.DecideRawAsync(new JsonObject { ["state"] = new string('a', DecisionNodeClient.MaxBodyBytes), ["questions"] = new JsonObject() });
        Assert.Single(handler.Requests);
    }

    private static byte[] Padded(int size)
    {
        var data = new byte[size];
        Fixtures.Read("rgb-7x3.png").CopyTo(data, 0);
        return data;
    }

    private static string Presign(Recorded r)
    {
        var files = (JsonArray)r.Json!["files"]!;
        var uploads = new JsonArray();
        var i = 0;
        foreach (var f in files)
        {
            uploads.Add(new JsonObject
            {
                ["upload_id"] = "upl_" + i.ToString("x32"),
                ["url"] = $"https://storage.example.test/obj{i}?X-Amz-Signature=abc",
                ["method"] = "PUT",
                ["headers"] = new JsonObject
                {
                    ["Content-Type"] = (string)f!["content_type"]!,
                    ["Content-MD5"] = (string)f["checksum"]!,
                    ["If-None-Match"] = "*",
                    ["x-amz-checksum-sha256"] = (string)f["sha256"]!,
                },
                ["expires_at"] = "2099-01-01T00:00:00Z",
            });
            i++;
        }
        return new JsonObject { ["uploads"] = uploads, ["expires_in"] = 300 }.ToJsonString();
    }
}
