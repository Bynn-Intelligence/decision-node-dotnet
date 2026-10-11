using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Sixteen images a request, videos sent as frames, and the video fields of a decision.</summary>
public class SpecOneTwoTests
{
    private static readonly byte[] Jpeg = Fixtures.Read("baseline-3x2.jpg");
    private static readonly byte[] Png = Fixtures.Read("rgb-7x3.png");

    private static VideoFrame JpegFrame(double t) => VideoFrame.FromBytes(t, Jpeg);

    private static Video Cam(params double[] times) => Video.Of("cam", times.Select(JpegFrame));

    private static async Task<BadRequestError> Refused(Func<DecisionNodeClient, Task> call, StubHandler? stub = null)
    {
        stub ??= new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<BadRequestError>(() => call(dn));
        Assert.Equal("api_usage_error", error.ErrorType);
        Assert.Null(error.Status);
        Assert.Null(error.RequestId);
        Assert.Empty(stub.Requests);
        return error;
    }

    [Fact]
    public async Task SixteenImagesGoInlineAndTheSeventeenthIsRefused()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("s", OneTruth(), Enumerable.Range(0, 16).Select(i => Image.FromBytes("i" + i, Png)));
        Assert.Equal(16, ((JsonArray)Assert.Single(stub.Requests).Json!["images"]!).Count);

        var refused = new StubHandler((_, _) => Ok());
        using var other = Client(refused);
        var images = new[] { Image.FromBytes("a", Png), Image.FromUploadId("b", "upl_1", "image/png") }
            .Concat(Enumerable.Range(0, 15).Select(i => Image.FromUrl("u" + i, "https://x.example/" + i)));
        var error = await Assert.ThrowsAsync<ValidationError>(() => other.DecideAsync("s", OneTruth(), images));
        Assert.Null(error.Status);
        Assert.Null(error.RequestId);
        Assert.Empty(refused.Requests);
        var entry = (JsonObject)Assert.Single(error.Entries!)!;
        Assert.Equal("too_long", (string?)entry["type"]);
        Assert.Equal("""["body","images"]""", entry["loc"]!.ToJsonString());
        Assert.Equal("List should have at most 16 items after validation, not 17", (string?)entry["msg"]);
        Assert.Equal("""{"field_type":"List","max_length":16,"actual_length":17}""", entry["ctx"]!.ToJsonString());
        var listed = (JsonArray)entry["input"]!;
        Assert.Equal("""{"id":"a","media_type":"image/png"}""", listed[0]!.ToJsonString());
        Assert.Equal("""{"id":"b","upload_id":"upl_1","media_type":"image/png"}""", listed[1]!.ToJsonString());
        Assert.Equal("""{"id":"u0","url":"https://x.example/0"}""", listed[2]!.ToJsonString());
    }

    [Fact]
    public async Task CountsJoinTheRequestsOtherEntriesImagesBeforeVideos()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var videos = Enumerable.Range(0, 5).Select(i => Video.Of("v" + i, new[] { JpegFrame(0) })).ToArray();
        var error = await Assert.ThrowsAsync<ValidationError>(() => dn.DecideAsync(
            "s",
            new Dictionary<string, Question> { ["route"] = new JsonObject { ["type"] = "choice", ["instructions"] = "Where?" } },
            Enumerable.Range(0, 17).Select(i => Image.FromUrl("u" + i, "https://x.example/" + i)),
            videos));
        Assert.Empty(stub.Requests);
        var entries = error.Entries!.Select(e => (string)e!["type"]! + " " + e["loc"]!.ToJsonString()).ToList();
        Assert.Equal(new[]
        {
            """missing ["body","questions","route","choice","criteria"]""",
            """too_long ["body","images"]""",
            """too_long ["body","videos"]""",
        }, entries);
        var videoEntry = (JsonObject)error.Entries![2]!;
        Assert.Equal("List should have at most 4 items after validation, not 5", (string?)videoEntry["msg"]);
        Assert.True(JsonNode.DeepEquals(videos[4].ToJson(), videoEntry["input"]![4]));
    }

    [Fact]
    public async Task AHeldLowerImageLimitIsTheMaxLength()
    {
        var stub = new StubHandler((r, _) => r.Uri.AbsolutePath == "/v1/models"
            ? Ok("""{"models":[{"name":"decisionnode-moe","description":"d","release_date":"2026-10-08","images":{"max_images":2}}]}""")
            : Ok());
        using var dn = Client(stub);
        await dn.ModelsAsync();
        var error = await Assert.ThrowsAsync<ValidationError>(() => dn.DecideAsync("s", OneTruth(), Enumerable.Range(0, 3).Select(i => Image.FromUrl("u" + i, "https://x.example/" + i))));
        Assert.Equal(2, (long)error.Entries![0]!["ctx"]!["max_length"]!);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task AVideoIsSentInTheWireFormWithItsFramesInline()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var video = Video.Of("dock", new[]
        {
            VideoFrame.FromBytes(30, Jpeg),
            VideoFrame.FromBytes(30.5, Png, "image/png"),
            VideoFrame.FromBase64(31, "UklGRg==", "image/webp"),
        }, fps: 2, start: 30, end: 31.5);
        await dn.DecideAsync("Dock.", OneTruth(), videos: new[] { video });

        var body = (JsonObject)Assert.Single(stub.Requests).Json!;
        Assert.False(body.ContainsKey("images"));
        var sent = (JsonObject)Assert.Single((JsonArray)body["videos"]!)!;
        Assert.Equal(new[] { "id", "fps", "start", "end", "frames" }, sent.Select(kv => kv.Key));
        Assert.Equal("dock", (string?)sent["id"]);
        Assert.Equal(2.0, (double)sent["fps"]!);
        Assert.Equal(31.5, (double)sent["end"]!);
        var frames = (JsonArray)sent["frames"]!;
        Assert.Equal(new[] { "t", "media_type", "data" }, ((JsonObject)frames[0]!).Select(kv => kv.Key));
        Assert.Equal("image/jpeg", (string?)frames[0]!["media_type"]);
        Assert.Equal(Convert.ToBase64String(Jpeg), (string?)frames[0]!["data"]);
        Assert.Equal("image/png", (string?)frames[1]!["media_type"]);
        Assert.Equal(30.5, (double)frames[1]!["t"]!);
        Assert.Equal("UklGRg==", (string?)frames[2]!["data"]);
    }

    [Fact]
    public async Task OptionalVideoFieldsAreLeftOutAndNoVideosMeansNoField()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("s", OneTruth(), videos: new[] { Cam(0) });
        await dn.DecideAsync("s", OneTruth(), videos: Array.Empty<Video>());
        var requests = stub.Requests.ToList();
        Assert.Equal(new[] { "id", "frames" }, ((JsonObject)requests[0].Json!["videos"]![0]!).Select(kv => kv.Key));
        Assert.False(((JsonObject)requests[1].Json!).ContainsKey("videos"));
    }

    [Fact]
    public async Task ImagesComeBeforeVideosAndFramesNeverCountTowardTheThreshold()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = new DecisionNodeClient(new DecisionNodeClientOptions
        {
            ApiKey = "dn_test_key",
            BaseUrl = "https://api.example.test",
            HttpClient = new HttpClient(stub),
            DelegatingHandlersNeverResendRequests = true,
            InlineThreshold = 1000,
        });
        // The frames total far more than the threshold; the one small image still goes inline.
        var big = new byte[4000];
        Jpeg.CopyTo(big, 0);
        var video = Video.Of("cam", new[] { VideoFrame.FromBytes(0, big), VideoFrame.FromBytes(1, big) });
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromBytes("still", Png) }, new[] { video });
        var body = (JsonObject)Assert.Single(stub.Requests).Json!;
        Assert.Equal(new[] { "model", "state", "questions", "images", "videos" }, body.Select(kv => kv.Key));
        Assert.NotNull(body["images"]![0]!["data"]);
    }

    [Fact]
    public async Task AWireFormVideoIsSentAsGiven()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var wire = JsonNode.Parse("""{"id":"cam","fps":1,"frames":[{"t":0,"media_type":"image/jpeg","data":"AAAA"},{"t":1,"media_type":"image/jpeg","data":"AAAA"}]}""")!.AsObject();
        await dn.DecideAsync("s", OneTruth(), videos: new[] { Video.FromJson(wire), Video.Of("v2", new[] { VideoFrame.FromJson(JsonNode.Parse("""{"t":2,"media_type":"image/png","data":"BBBB"}""")!.AsObject()) }) });
        var videos = (JsonArray)Assert.Single(stub.Requests).Json!["videos"]!;
        Assert.True(JsonNode.DeepEquals(wire, videos[0]));
        Assert.Equal("BBBB", (string?)videos[1]!["frames"]![0]!["data"]);
    }

    public static IEnumerable<object?[]> VideoFaults()
    {
        var frame = JpegFrame(0);
        yield return new object?[] { new[] { Video.Of("", new[] { frame }) }, null, "Video 0 needs an id." };
        yield return new object?[] { new[] { Video.Of("cam", new[] { frame }), Video.Of("cam", new[] { frame }) }, null, "Video id 'cam' is used twice (ids are unique across images and videos)." };
        yield return new object?[] { new[] { Video.Of("gate", new[] { frame }) }, Image.FromUrl("gate", "https://x.example/g.jpg"), "Video id 'gate' is used twice (ids are unique across images and videos)." };
        yield return new object?[] { new[] { Video.Of("cam", Array.Empty<VideoFrame>()) }, null, "Video 'cam': frames must be a non-empty list of frames." };
        yield return new object?[] { new[] { Cam(-0.5) }, null, "Video 'cam': frame 0: t must be a number of seconds, at least 0." };
        yield return new object?[] { new[] { Cam(double.NaN) }, null, "Video 'cam': frame 0: t must be a number of seconds, at least 0." };
        yield return new object?[] { new[] { Cam(0, 1.5, 1) }, null, "Video 'cam': frame 2: t must be larger than the frame before it (1.5)." };
        yield return new object?[] { new[] { Cam(1.5, 1.5) }, null, "Video 'cam': frame 1: t must be larger than the frame before it (1.5)." };
        yield return new object?[] { new[] { Cam(1, 0.5) }, null, "Video 'cam': frame 1: t must be larger than the frame before it (1)." };
        yield return new object?[] { new[] { Video.Of("cam", new[] { VideoFrame.FromBase64(0, "AAAA", null) }) }, null, "Video 'cam': frame 0: media_type must be one of image/jpeg, image/png, image/webp." };
        yield return new object?[] { new[] { Video.Of("cam", new[] { frame, VideoFrame.FromBytes(0.5, Fixtures.Read("pixel.gif")) }) }, null, "Video 'cam': frame 1: media_type must be one of image/jpeg, image/png, image/webp." };
        yield return new object?[] { new[] { Video.Of("cam", new[] { VideoFrame.FromBase64(0, "R0lG", "image/gif") }) }, null, "Video 'cam': frame 0: media_type must be one of image/jpeg, image/png, image/webp." };
        yield return new object?[] { new[] { Video.Of("cam", new[] { frame }, fps: 0) }, null, "Video 'cam': fps must be a number above 0 and at most 4." };
        yield return new object?[] { new[] { Video.Of("cam", new[] { frame }, fps: 5) }, null, "Video 'cam': fps must be a number above 0 and at most 4." };
        yield return new object?[] { new[] { Video.Of("cam", new[] { frame }, start: -1) }, null, "Video 'cam': start must be a number of seconds, at least 0." };
        yield return new object?[] { new[] { Video.Of("cam", new[] { frame }, end: -1) }, null, "Video 'cam': end must be a number of seconds, at least 0." };
        yield return new object?[] { new[] { Video.Of("cam", new[] { frame }, start: 2, end: 2) }, null, "Video 'cam': end must be after start." };
        yield return new object?[] { new[] { Video.Of("cam", new[] { frame }, end: 0) }, null, "Video 'cam': end must be after start." };
        yield return new object?[] { new[] { Video.FromJson(JsonNode.Parse("""{"id":"cam","frames":[{"t":"0","media_type":"image/jpeg","data":"AAAA"}]}""")!.AsObject()) }, null, "Video 'cam': frame 0: t must be a number of seconds, at least 0." };
        yield return new object?[] { new[] { Video.FromJson(JsonNode.Parse("""{"id":"cam","fps":true,"frames":[{"t":0,"media_type":"image/jpeg","data":"AAAA"}]}""")!.AsObject()) }, null, "Video 'cam': fps must be a number above 0 and at most 4." };
        yield return new object?[] { new[] { Video.FromJson(JsonNode.Parse("""{"id":"cam","frames":[{"t":0,"media_type":"image/jpeg"}]}""")!.AsObject()) }, null, "Video 'cam': frame 0: data must be base64 text." };
        yield return new object?[] { new[] { Video.FromJson(JsonNode.Parse("""{"id":"cam","frames":[7]}""")!.AsObject()) }, null, "Video 'cam': frame 0 must be an object with t, media_type and data." };
        yield return new object?[] { new[] { Video.FromJson(JsonNode.Parse("""{"id":"cam","url":"https://x.example/v.mp4","frames":[{"t":0,"media_type":"image/jpeg","data":"AAAA"}]}""")!.AsObject()) }, null, "Video 'cam': needs exactly one of data, url and frames." };
        yield return new object?[] { new[] { Video.FromJson(JsonNode.Parse("""{"id":"cam","url":"https://x.example/v.mp4"}""")!.AsObject()) }, null, "Video 'cam': frames must be a non-empty list of frames." };
    }

    [Theory]
    [MemberData(nameof(VideoFaults))]
    public async Task VideoRulesAreCheckedBeforeSending(Video[] videos, Image? image, string message)
    {
        var images = image is null ? null : new[] { image };
        var error = await Refused(c => c.DecideAsync("s", OneTruth(), images, videos));
        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(4, "4")]
    [InlineData(1, "1")]
    [InlineData(0.1, "0.1")]
    [InlineData(1.0 / 3, "0.333333")]
    [InlineData(1000.5, "1000.5")]
    [InlineData(1234567.5, "1.23457e+06")]
    [InlineData(1e6, "1e+06")]
    [InlineData(999999.5, "1e+06")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001234, "1.234e-05")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e-100, "1e-100")]
    [InlineData(2.5e7, "2.5e+07")]
    [InlineData(123456.75, "123457")]
    public void NumbersInVideoMessagesAreWrittenAsTheApiWritesThem(double value, string text)
    {
        Assert.Equal(text, Internal.LocalChecks.Printf(value));
    }

    [Fact]
    public async Task ALargeFrameTimeIsWrittenWithSixSignificantDigits()
    {
        var error = await Refused(c => c.DecideAsync("s", OneTruth(), videos: new[] { Cam(1234567.5, 1) }));
        Assert.Equal("Video 'cam': frame 1: t must be larger than the frame before it (1.23457e+06).", error.Message);
    }

    [Fact]
    public async Task VideosAreCheckedInEveryCallThatTakesThem()
    {
        var bad = new[] { Video.Of("cam", new[] { JpegFrame(0) }, fps: 5) };
        const string message = "Video 'cam': fps must be a number above 0 and at most 4.";
        Assert.Equal(message, (await Refused(c => c.CheckAsync("s", "?", videos: bad))).Message);
        Assert.Equal(message, (await Refused(c => c.ClassifyAsync("s", "?", new[] { "a", "b" }, videos: bad))).Message);
        Assert.Equal(message, (await Refused(c => c.ScoreAsync("s", "?", new object?[] { "lo", "hi" }, videos: bad))).Message);
        Assert.Equal(message, (await Refused(c => c.NumberAsync("s", "?", videos: bad))).Message);
        Assert.Equal(message, (await Refused(c => c.GateAsync("deploy", videos: bad))).Message);
        Assert.Equal(message, (await Refused(c => c.DecideManyAsync(new[]
        {
            new DecideRequest { Questions = OneTruth(), Videos = new[] { Cam(0) } },
            new DecideRequest { Questions = OneTruth(), Videos = bad },
        }))).Message);
        Assert.Equal(message, (await Refused(c => c.Batches.CreateAsync(new[]
        {
            new BatchRequest { CustomId = "a", Questions = OneTruth(), Videos = bad },
        }))).Message);
        using var dn = Client(new StubHandler((_, _) => Ok()));
        Assert.Equal(message, Assert.Throws<BadRequestError>(() => dn.CheckRequest(new DecideRequest { Questions = OneTruth(), Videos = bad })).Message);
        Assert.Equal(message, Assert.Throws<BadRequestError>(() => dn.Batches.CheckCreate(new[] { new BatchRequest { CustomId = "a", Questions = OneTruth(), Videos = bad } })).Message);
        dn.CheckRequest(new DecideRequest { Questions = OneTruth(), Images = Enumerable.Range(0, 16).Select(i => Image.FromBytes("i" + i, Png)).ToList(), Videos = new[] { Cam(0, 0.5) } });
    }

    [Fact]
    public async Task FrameCountsAndFrameBytesAreLeftToTheServer()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var many = Video.Of("cam", Enumerable.Range(0, 1100).Select(i => VideoFrame.FromBase64(i * 0.5, "not an image", "image/png")));
        await dn.DecideAsync("s", OneTruth(), videos: new[] { many });
        Assert.Equal(1100, ((JsonArray)Assert.Single(stub.Requests).Json!["videos"]![0]!["frames"]!).Count);
    }

    [Fact]
    public async Task ABatchLineCarriesItsVideosAfterItsImages()
    {
        var stub = new StubHandler((r, _) => Json(201, """{"id":"batch_0123456789","state":"draft","counters":{}}"""));
        using var dn = Client(stub);
        await dn.Batches.CreateAsync(new[]
        {
            new BatchRequest { CustomId = "clip-1", Questions = OneTruth(), Images = new[] { Image.FromBytes("still", Png) }, Videos = new[] { Cam(0, 0.5) } },
        });
        var line = JsonNode.Parse(Assert.Single(stub.Requests).Body!.TrimEnd('\n'))!.AsObject();
        Assert.Equal(new[] { "custom_id", "model", "state", "questions", "images", "videos" }, line.Select(kv => kv.Key));
        Assert.Equal(2, ((JsonArray)line["videos"]![0]!["frames"]!).Count);
    }

    [Fact]
    public async Task VideoUsageAndWarningsAreReadAsSent()
    {
        var body = """{"model":"m","answers":{"q":{"type":"truth","truth":0.5}},"usage":{"input_tokens":4130,"output_tokens":0,"video_frames":20,"video_seconds":10.25},"warnings":["Video 'cam' was read at 1 frame a second."]}""";
        var stub = new StubHandler((_, _) => Ok(body));
        using var dn = Client(stub);
        var d = await dn.DecideAsync("s", OneTruth(), videos: new[] { Cam(0) });
        Assert.Equal(20, d.Usage.VideoFrames);
        Assert.Equal(10.25, d.Usage.VideoSeconds);
        Assert.Equal(new[] { "Video 'cam' was read at 1 frame a second." }, d.Warnings);

        using var other = Client(new StubHandler((_, _) => Ok()));
        var plain = await other.DecideAsync("s", OneTruth());
        Assert.Null(plain.Usage.VideoFrames);
        Assert.Null(plain.Usage.VideoSeconds);
        Assert.Empty(plain.Warnings);
        Assert.Equal(9, plain.Usage.InputTokens);
    }
}
