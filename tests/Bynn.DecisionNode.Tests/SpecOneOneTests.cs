using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Number questions, truth read from its other wire name, /v1/systemone and the local checks.</summary>
public class SpecOneOneTests
{
    [Fact]
    public void NumberBuilderLeavesOutWhatIsNotGiven()
    {
        Assert.Equal("""{"type":"number","instructions":"How many?"}""", Question.Number("How many?").ToString());
        Assert.Equal("""{"type":"number","instructions":"Rating?","min":0,"max":5,"step":0.5}""", Question.Number("Rating?", 0, 5, 0.5).ToString());
        Assert.Equal("""{"type":"number","instructions":"t","min":-40.5,"max":10}""", Question.Number("t", -40.5, 10).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => Question.Number("x", double.NaN));
    }

    [Fact]
    public async Task NumberAnswersKeepTheServersKeysAndNumbers()
    {
        var body = """{"model":"m","answers":{"number":{"type":"number","number":3.5,"expected":3.42,"confidence":0.61,"probabilities":{"2.5":0.04,"3":0.21,"3.5":0.612}}}}""";
        var stub = new StubHandler((_, _) => Ok(body));
        using var dn = Client(stub);
        var answer = await dn.NumberAsync("s", "Rating?", 0, 5, 0.5);
        Assert.Equal(3.5, answer.Number);
        Assert.Equal(3.42, answer.Expected);
        Assert.Equal(0.61, answer.Confidence);
        Assert.Equal(new[] { "2.5", "3", "3.5" }, answer.Probabilities.Keys);
        Assert.Equal("number", (string?)Assert.Single(stub.Requests).Json!["questions"]!["number"]!["type"]);
    }

    [Fact]
    public async Task AMalformedNumberAnswerIsUnknown()
    {
        var stub = new StubHandler((_, _) => Ok("""{"answers":{"q":{"type":"number","number":"7"}}}"""));
        using var dn = Client(stub);
        var d = await dn.DecideAsync("s", OneTruth());
        Assert.IsType<UnknownAnswer>(d["q"]);
        await Assert.ThrowsAsync<ServerError>(() => dn.NumberAsync("s", "How many?"));
    }

    [Fact]
    public async Task ANoulAnswerIsATruthAnswer()
    {
        var stub = new StubHandler((_, _) => Ok("""{"answers":{"q":{"type":"noul","noul":0.92},"bad":{"type":"noul"}}}"""));
        using var dn = Client(stub);
        var d = await dn.DecideAsync("s", OneTruth());
        Assert.Equal(0.92, d.Truth("q").Truth);
        Assert.IsType<UnknownAnswer>(d["bad"]);
    }

    [Fact]
    public async Task SystemoneWritesTruthAsItsWireTypeAndAlwaysSendsAModel()
    {
        var stub = new StubHandler((_, _) => Ok("""{"answers":{"q":{"type":"noul","noul":0.3}}}"""));
        using var dn = Client(stub);
        var d = await dn.SystemoneAsync("s", new Dictionary<string, Question>
        {
            ["q"] = Question.Truth("Is it?"),
            ["c"] = Question.Choice("Which?", new[] { "a", "b" }),
        });
        var sent = Assert.Single(stub.Requests);
        Assert.Equal("/v1/systemone", sent.Uri.AbsolutePath);
        Assert.Equal("decisionnode-latest", (string?)sent.Json!["model"]);
        Assert.Equal("noul", (string?)sent.Json["questions"]!["q"]!["type"]);
        Assert.Equal("choice", (string?)sent.Json["questions"]!["c"]!["type"]);
        Assert.Equal(0.3, d.Truth("q").Truth);
    }

    public static IEnumerable<object[]> LocalFaults()
    {
        yield return new object[] { Question.Choice("?", Array.Empty<string>()), typeof(BadRequestError) };
        yield return new object[] { Question.Choice("?", Enumerable.Range(0, 256).Select(i => "o" + i)), typeof(BadRequestError) };
        yield return new object[] { Question.Score("?", Enumerable.Range(0, 11).Select(i => (object?)("l" + i))), typeof(BadRequestError) };
        yield return new object[] { Question.Score("?", Array.Empty<object?>()), typeof(ValidationError) };
        yield return new object[] { Question.Truth(), typeof(BadRequestError) };
        yield return new object[] { Question.Number("?", 60, 50), typeof(ValidationError) };
        yield return new object[] { Question.Number("?", 0, 10, 0), typeof(ValidationError) };
        yield return new object[] { Question.Number("?", 0, 1000, 0.1), typeof(ValidationError) };
        yield return new object[] { Question.Number(null, 0, 10), typeof(BadRequestError) };
        yield return new object[] { (Question)new JsonObject { ["type"] = "number", ["instructions"] = "?", ["min"] = "5" }, typeof(ValidationError) };
    }

    [Theory]
    [MemberData(nameof(LocalFaults))]
    public async Task FixedQuestionLimitsAreCheckedBeforeSending(Question question, Type errorClass)
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var error = await Assert.ThrowsAnyAsync<DecisionNodeError>(() => dn.DecideAsync("s", new Dictionary<string, Question> { ["q"] = question }));
        Assert.Equal(errorClass, error.GetType());
        Assert.Null(error.Status);
        Assert.Null(error.RequestId);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task TheLargestGridAndTheLimitsThemselvesPass()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("s", new Dictionary<string, Question>
        {
            ["n"] = Question.Number("?", 0, 4095),
            ["half"] = Question.Number("?", 0, 2047.5, 0.5),
            ["c"] = Question.Choice("?", Enumerable.Range(0, 255).Select(i => "o" + i)),
            ["s"] = Question.Score("?", Enumerable.Range(0, 10).Select(i => (object?)("l" + i))),
            ["t"] = Question.Truth(ifTrue: "yes"),
        });
        Assert.Single(stub.Requests);
    }

    public static IEnumerable<object[]> ImageFaults()
    {
        var png = Fixtures.Read("rgb-7x3.png");
        yield return new object[] { Enumerable.Range(0, 17).Select(i => Image.FromBytes("i" + i, png)).ToArray() };
        yield return new object[] { new[] { Image.FromBytes("a", png) }.Concat(Enumerable.Range(1, 16).Select(i => Image.FromUrl("u" + i, "https://x.example/" + i))).ToArray() };
        yield return new object[] { new[] { Image.FromBytes("a", png), Image.FromBytes("a", png) } };
        yield return new object[] { new[] { Image.FromUrl("u", "http://x.example/r.jpg") } };
        yield return new object[] { new[] { Image.FromBytes("g", Fixtures.Read("pixel.gif"), "image/gif") } };
        yield return new object[] { new[] { Image.FromBytes("j", png, "image/jpeg") } };
        var big = new byte[10 * 1024 * 1024 + 1];
        png.CopyTo(big, 0);
        yield return new object[] { new[] { Image.FromBytes("big", big) } };
    }

    [Theory]
    [MemberData(nameof(ImageFaults))]
    public async Task ImageRulesAreCheckedBeforeSending(Image[] images)
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync("s", OneTruth(), images));
        Assert.Equal("api_usage_error", error.ErrorType);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("https://files.example.com:8443/r.jpg")]
    [InlineData("https://user:secret@files.example.com/r.jpg")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://10.1.2.3/r.jpg")]
    [InlineData("https://[::1]/r.jpg")]
    [InlineData("https://[::ffff:127.0.0.1]/r.jpg")]
    [InlineData("https://[64:ff9b::a9fe:a9fe]/r.jpg")]
    [InlineData("https:///r.jpg")]
    public async Task UrlsTheServerWouldRefuseAreRefusedLocally(string url)
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync("s", OneTruth(), new[] { Image.FromUrl("u", url) }));
        Assert.Equal("image_url_refused", error.ErrorType);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("https://files.example.com/r.jpg")]
    [InlineData("https://files.example.com:443/r.jpg")]
    [InlineData("https://8.8.8.8/r.jpg")]
    [InlineData("https://10.example.com/r.jpg")]
    public async Task OrdinaryUrlsPass(string url)
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromUrl("u", url) });
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task NumberGridFaultsCarryTheServersEntries()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<ValidationError>(() => dn.DecideAsync("s", new Dictionary<string, Question> { ["n"] = Question.Number("?", 60, 50) }));
        Assert.Null(error.ErrorType);
        Assert.Equal(
            """[{"type":"less_than_equal","loc":["body","questions","n","number","min"],"msg":"Input should be less than or equal to 50","input":60,"ctx":{"le":50}}]""",
            error.Entries!.ToJsonString());
    }

    [Fact]
    public async Task NestingOver128IsRefusedAndAt128Passes()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        JsonNode Nest(int depth)
        {
            JsonNode node = "s";
            for (var i = 0; i < depth; i++)
            {
                node = new JsonArray(node);
            }
            return node;
        }
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync(Nest(128), OneTruth()));
        Assert.Equal("Invalid request.", error.Message);
        await dn.DecideAsync(Nest(127), OneTruth());
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task AnUnknownQuestionTypeIsRefusedAndNumberOnSystemone()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var unknown = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync("s", new Dictionary<string, Question> { ["q"] = new JsonObject { ["type"] = "rank" } }));
        Assert.Equal("api_usage_error", unknown.ErrorType);
        await Assert.ThrowsAsync<BadRequestError>(() => dn.SystemoneAsync("s", new Dictionary<string, Question> { ["n"] = Question.Number("How many?") }));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task OneFaultyRequestStopsDecideManyBeforeAnythingIsSent()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideManyAsync(new[]
        {
            new DecideRequest { Questions = OneTruth() },
            new DecideRequest { Questions = OneTruth(), Images = new[] { Image.FromBytes("g", Fixtures.Read("pixel.gif")) } },
        }));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task ASessionLimitIsNeverRetried()
    {
        var stub = new StubHandler((_, _) => Json(429, """{"detail":{"error_type":"session_limit_exceeded","message":"limit"}}""", ("retry-after", "5")));
        using var dn = Client(stub, maxRetries: 3);
        var error = await Assert.ThrowsAsync<SessionLimitError>(() => dn.OpenSessionAsync(null, OneTruth()));
        Assert.IsAssignableFrom<RateLimitError>(error);
        Assert.Equal(TimeSpan.FromSeconds(5), error.RetryAfter);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task OpenSessionSendsOnlyTheFieldsGivenAndAlwaysAModel()
    {
        var open = """{"session_id":"ses_000000000000000000000001","model":"decisionnode-1.0","stream_url":"wss://api.example.test/v1/sessions/ses_000000000000000000000001/stream","questions":["q"],"answers":{"q":{"type":"truth","truth":0.1}}}""";
        var stub = new StubHandler((_, _) => Ok(open));
        using var dn = Client(stub);
        var session = await dn.OpenSessionAsync(null, OneTruth(), window: 4);
        Assert.Equal("""{"model":"decisionnode-latest","questions":{"q":{"type":"truth","instructions":"?"}},"window":4}""", Assert.Single(stub.Requests).Body);
        Assert.Equal("ses_000000000000000000000001", session.SessionId);
        Assert.Equal(0.1, session.Answers["q"].AsTruth().Truth);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync("frame"));
    }

    [Fact]
    public async Task APlainTextStreamUrlIsRefusedBeforeTheKeyIsSent()
    {
        var open = """{"session_id":"ses_1","stream_url":"ws://api.example.test/v1/sessions/ses_1/stream"}""";
        var stub = new StubHandler((_, _) => Ok(open));
        using var dn = Client(stub);
        var session = await dn.OpenSessionAsync(null, OneTruth());
        await Assert.ThrowsAsync<DecisionNodeError>(() => session.ConnectAsync());
    }

    [Fact]
    public void CheckRequestRunsTheChecksAndSendsNothing()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var big = new byte[600_000];
        Fixtures.Read("rgb-7x3.png").CopyTo(big, 0);
        dn.CheckRequest(new DecideRequest { State = "s", Questions = OneTruth(), Images = new[] { Image.FromBytes("scan", big) } });
        dn.CheckSystemoneRequest(new DecideRequest { Questions = OneTruth() });
        dn.CheckRequest(new SessionOptions { Questions = OneTruth(), Window = 64, TtlSeconds = 3600 });
        dn.Batches.CheckCreate(Array.Empty<BatchRequest>());
        Assert.Throws<ValidationError>(() => dn.CheckRequest(new DecideRequest { Questions = new Dictionary<string, Question> { ["n"] = Question.Number("?", 60, 50) } }));
        Assert.Throws<BadRequestError>(() => dn.CheckSystemoneRequest(new DecideRequest { Questions = new Dictionary<string, Question> { ["n"] = Question.Number("?") } }));
        Assert.Throws<ValidationError>(() => dn.CheckRequest(new SessionOptions { Questions = OneTruth(), Window = 65 }));
        var repeated = Assert.Throws<BadRequestError>(() => dn.Batches.CheckAdd(new[]
        {
            new BatchRequest { CustomId = "a", Questions = OneTruth() },
            new BatchRequest { CustomId = "a", Questions = OneTruth() },
        }));
        Assert.Equal("Line 2: custom_id \"a\" is already in the batch.", repeated.Message);
        Assert.Throws<BadRequestError>(() => dn.Batches.CheckAdd(Array.Empty<BatchRequest>()));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task HeldLimitsAreFoundByNameAliasOrVersion()
    {
        const string models = """{"models":[{"name":"decisionnode-1.0","version":"decisionnode-1.0","alias":"decisionnode-latest","sessions":{"max_window":16}},{"name":"decisionnode-1.0-flash","version":"decisionnode-1.0-flash","alias":"decisionnode-flash-latest","number_max_grid_values":0}]}""";
        var stub = new StubHandler((_, _) => Ok(models));
        using var dn = Client(stub);
        await dn.ModelsAsync();
        Assert.Throws<ValidationError>(() => dn.CheckRequest(new SessionOptions { Questions = OneTruth(), Window = 17, Model = "decisionnode-latest" }));
        Assert.Throws<ValidationError>(() => dn.CheckRequest(new SessionOptions { Questions = OneTruth(), Window = 17, Model = "decisionnode-1.0" }));
        Assert.Throws<BadRequestError>(() => dn.CheckRequest(new DecideRequest { Questions = new Dictionary<string, Question> { ["n"] = Question.Number("?") }, Model = "decisionnode-flash-latest" }));
        // A model the list does not state holds nothing: left to the server.
        dn.CheckRequest(new SessionOptions { Questions = OneTruth(), Window = 17, Model = "decisionnode-9" });
    }

    [Fact]
    public void UploadTimeoutDefaultsTo60SecondsAndMustBeAboveZero()
    {
        using var dn = new DecisionNodeClient(apiKey: "k");
        Assert.Equal(TimeSpan.FromSeconds(60), dn.UploadTimeout);
        using var custom = new DecisionNodeClient(apiKey: "k", uploadTimeout: TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), custom.UploadTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionNodeClient(apiKey: "k", uploadTimeout: TimeSpan.Zero));
    }

    [Theory]
    [InlineData(1001, true)]
    [InlineData(1006, true)]
    [InlineData(1011, true)]
    [InlineData(4404, true)]
    [InlineData(4408, true)]
    [InlineData(4409, true)]
    [InlineData(4410, true)]
    [InlineData(1000, false)]
    [InlineData(1008, false)]
    [InlineData(1013, false)]
    [InlineData(4401, false)]
    [InlineData(4402, false)]
    public void CloseCodesSayWhetherToReopen(int code, bool reopen)
    {
        var closed = (SessionClosed)Activator.CreateInstance(typeof(SessionClosed), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new object[] { code, "" }, null)!;
        Assert.Equal(reopen, closed.Reopen);
    }
}
