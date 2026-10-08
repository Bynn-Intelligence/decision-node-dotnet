using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Question;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

public class ClientTests
{
    [Fact]
    public async Task DecideSendsTheWireForm()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync(new { ticket = "charged twice" }, new Dictionary<string, Question>
        {
            ["route"] = Choice("Where?", new Dictionary<string, string> { ["billing"] = "money", ["bug"] = "broken" }),
            ["escalate"] = Truth("Escalate?"),
        });
        var r = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://api.example.test/v1/decide", r.Uri.ToString());
        Assert.Equal(
            """{"model":"decisionnode-latest","state":{"ticket":"charged twice"},"questions":{"route":{"type":"choice","instructions":"Where?","criteria":{"billing":"money","bug":"broken"}},"escalate":{"type":"truth","instructions":"Escalate?"}}}""",
            r.Body);
    }

    [Fact]
    public async Task EveryRequestCarriesKeyUserAgentAndContentType()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub, headers: new Dictionary<string, string> { ["x-team"] = "support" });
        await dn.DecideAsync("s", OneTruth());
        var h = Assert.Single(stub.Requests).Headers;
        Assert.Equal("Bearer dn_test_key", h["Authorization"]);
        Assert.Equal("decisionnode-dotnet/" + DecisionNodeClient.Version, h["User-Agent"]);
        Assert.Equal("application/json", h["Content-Type"]);
        Assert.Equal("support", h["x-team"]);
        Assert.False(h.ContainsKey("x-decisionnode-safety-body"));
    }

    [Fact]
    public async Task NullStateIsSentAsEmptyString()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync(null, OneTruth());
        Assert.Equal("", (string?)Assert.Single(stub.Requests).Json!["state"]);
    }

    [Fact]
    public async Task ModelPerCallBeatsClientModel()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub, model: "decisionnode-1.0");
        await dn.DecideAsync("s", OneTruth());
        await dn.DecideAsync("s", OneTruth(), model: "decisionnode-1.0-flash");
        var models = stub.Requests.Select(r => (string?)r.Json!["model"]).ToArray();
        Assert.Equal(new[] { "decisionnode-1.0", "decisionnode-1.0-flash" }, models);
    }

    [Fact]
    public async Task ImagesTravelBesideTheState()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("", OneTruth(), new[]
        {
            Image.FromBytes("a", Fixtures.Read("rgb-7x3.png"), "image/png"),
            Image.FromUrl("b", "https://example.com/r.jpg"),
        });
        var images = Assert.Single(stub.Requests).Json!["images"]!.ToJsonString();
        var data = Convert.ToBase64String(Fixtures.Read("rgb-7x3.png"));
        Assert.Equal($$"""[{"id":"a","media_type":"image/png","data":"{{data}}"},{"id":"b","url":"https://example.com/r.jpg"}]""", images);
    }

    [Fact]
    public async Task HelpersUseFixedQuestionIds()
    {
        var stub = new StubHandler((r, _) =>
        {
            var id = ((JsonObject)r.Json!["questions"]!).Single().Key;
            return Ok(id switch
            {
                "check" => """{"answers":{"check":{"type":"truth","truth":0.97}}}""",
                "classify" => """{"answers":{"classify":{"type":"choice","choice":"a","confidence":0.4,"probabilities":{"a":0.7,"b":0.3}}}}""",
                _ => """{"answers":{"score":{"type":"score","score":1.5,"confidence":0.3,"legend":{"0":"lo","1":"mid","2":"hi"},"probabilities":{"0":0.1,"1":0.3,"2":0.6}}}}""",
            });
        });
        using var dn = Client(stub);
        Assert.Equal(0.97, (await dn.CheckAsync("s", "Refund?")).Truth);
        Assert.Equal("a", (await dn.ClassifyAsync("s", "Which?", new[] { "a", "b" })).Choice);
        Assert.Equal(1.5, (await dn.ScoreAsync("s", "How?", new[] { "lo", "mid", "hi" })).Score);
    }

    [Theory]
    [InlineData("check", """{"answers":{"check":{"type":"truth","truth":"0.9"}}}""")]
    [InlineData("check", """{"answers":{}}""")]
    [InlineData("check", """{"answers":{"check":{"type":"choice","choice":"a","confidence":0.4,"probabilities":{"a":1}}}}""")]
    [InlineData("classify", """{"answers":{"classify":{"type":"choice","choice":"a","confidence":0.4}}}""")]
    [InlineData("score", """{"answers":{"score":{"type":"score","score":1,"confidence":0.5,"probabilities":[0.5,0.5],"legend":{"0":"lo","1":"hi"}}}}""")]
    public async Task AHelperWhoseOwnAnswerIsMalformedThrowsServerError(string helper, string body)
    {
        var stub = new StubHandler((_, _) => Ok(body, ("x-request-id", "req_h"), ("x-decisionnode-error", "the answer could not be completed")));
        using var dn = Client(stub, maxRetries: 0);
        Func<Task> call = helper switch
        {
            "check" => () => dn.CheckAsync("s", "?"),
            "classify" => () => dn.ClassifyAsync("s", "?", new[] { "a", "b" }),
            _ => () => dn.ScoreAsync("s", "?", new[] { "lo", "hi" }),
        };
        var e = await Assert.ThrowsAsync<ServerError>(call);
        Assert.Equal(200, e.Status);
        Assert.Equal("req_h", e.RequestId);
        Assert.Equal("the answer could not be completed", e.Reason);
    }

    [Fact]
    public async Task DecideKeepsAMalformedAnswerAsUnknown()
    {
        var stub = new StubHandler((_, _) => Ok("""{"answers":{"q":{"type":"truth","truth":"0.9"}}}"""));
        using var dn = Client(stub);
        var d = await dn.DecideAsync("s", OneTruth());
        Assert.IsType<UnknownAnswer>(d["q"]);
    }

    [Fact]
    public async Task GateSendsActionContextAndThreeQuestions()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.GateAsync("DROP TABLE invoices;", new { environment = "production" });
        var body = Assert.Single(stub.Requests).Json!;
        Assert.Equal("""{"action":"DROP TABLE invoices;","context":{"environment":"production"}}""", body["state"]!.ToJsonString());
        var qs = (JsonObject)body["questions"]!;
        Assert.Equal(new[] { "risk", "touches_production", "verdict" }, qs.Select(p => p.Key).ToArray());
        Assert.Equal("score", (string?)qs["risk"]!["type"]);
        Assert.Equal("truth", (string?)qs["touches_production"]!["type"]);
        Assert.Equal(new[] { "allow", "confirm", "block" }, ((JsonObject)qs["verdict"]!["criteria"]!).Select(p => p.Key).ToArray());
        Assert.Equal("How much harm could this action cause if it is wrong?", (string?)qs["risk"]!["instructions"]);
        Assert.Equal("Does this action change a production system, production data or anything customers use?", (string?)qs["touches_production"]!["instructions"]);
        Assert.Equal("Should this action run as it is, wait for a person to confirm it, or be blocked?", (string?)qs["verdict"]!["instructions"]);
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        var stub = new StubHandler((_, _) =>
        {
            var r = Json(307, """{"detail":{"error_type":"redirect","message":"Moved."}}""");
            r.Headers.Location = new Uri("https://elsewhere.example.test/v1/decide");
            return r;
        });
        using var dn = Client(stub);
        var e = await Assert.ThrowsAsync<DecisionNodeError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal(307, e.Status);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task EmptyModelOnACallUsesTheClientsModel()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub, model: "decisionnode-1.0");
        await dn.DecideAsync("s", OneTruth(), model: "");
        Assert.Equal("decisionnode-1.0", (string?)Assert.Single(stub.Requests).Json!["model"]);
    }

    [Fact]
    public void SpecVersionIsOnePointOne()
    {
        Assert.Equal("1.1", DecisionNodeClient.SpecVersion);
        Assert.Equal(524288, DecisionNodeClient.DefaultInlineThreshold);
    }

    [Fact]
    public async Task DecideRawSendsTheBodyExactlyAndReturnsParsedJson()
    {
        var stub = new StubHandler((_, _) => Ok("""{"future":{"p":0.8200000000000001}}"""));
        using var dn = Client(stub);
        var body = JsonNode.Parse("""{"state":"s","questions":{"q":{"type":"truth"}},"future_field":[1,2.5,null]}""")!;
        var result = await dn.DecideRawAsync(body);
        Assert.Equal(body.ToJsonString(), Assert.Single(stub.Requests).Body);
        Assert.Equal(0.8200000000000001, (double)result!["future"]!["p"]!);
    }

    [Fact]
    public async Task ModelsReadsNameDescriptionDateAndLimits()
    {
        var stub = new StubHandler((_, _) => Ok("""{"models":[{"name":"decisionnode-1.0","description":"Main.","release_date":"2026-10-01","max_input_tokens":32768},{"name":"decisionnode-latest"}]}"""));
        using var dn = Client(stub);
        var models = await dn.ModelsAsync();
        var r = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, r.Method);
        Assert.Null(r.Body);
        Assert.Equal("decisionnode-1.0", models[0].Name);
        Assert.Equal("2026-10-01", models[0].ReleaseDate);
        Assert.Equal(32768, (int)models[0].Limits["max_input_tokens"]!);
        Assert.Empty(models[1].Limits);
    }

    [Fact]
    public async Task MissingKeyFailsBeforeSending()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = new DecisionNodeClient(new DecisionNodeClientOptions { ApiKey = "", HttpClient = new HttpClient(stub), DelegatingHandlersNeverResendRequests = true });
        await Assert.ThrowsAsync<AuthenticationError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task EmptyQuestionMapFailsBeforeSending()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await Assert.ThrowsAsync<ArgumentException>(() => dn.DecideAsync("s", new Dictionary<string, Question>()));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task DebugLogNamesTheCallButNotTheContent()
    {
        var lines = new List<string>();
        var stub = new StubHandler((_, _) => Ok(headers: ("x-request-id", "req_1")));
        using var dn = Client(stub, log: lines.Add);
        await dn.DecideAsync("secret state", OneTruth("secret question"));
        var line = Assert.Single(lines);
        Assert.Contains("POST /v1/decide 200", line);
        Assert.Contains("req_1", line);
        Assert.DoesNotContain("secret", line);
        Assert.DoesNotContain("dn_test_key", line);
    }

    [Fact]
    public void ConstructorRejectsNegativeRetries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionNodeClient(apiKey: "k", maxRetries: -1));
    }
}

public class ClientArgumentTests
{
    private static DecisionNodeClient Make(string? baseUrl = null, TimeSpan? timeout = null, int? maxRetries = null, HttpClient? http = null, bool neverResend = false) =>
        new(new DecisionNodeClientOptions
        {
            ApiKey = "dn_test_key",
            BaseUrl = baseUrl,
            Timeout = timeout,
            MaxRetries = maxRetries,
            HttpClient = http ?? new HttpClient(new StubHandler((_, _) => Http.Ok())),
            DelegatingHandlersNeverResendRequests = neverResend || http is null,
        });

    [Theory]
    [InlineData("http://api.example.test")]
    [InlineData("http://10.0.0.5:8080")]
    [InlineData("http://localhost.example.test")]
    [InlineData("ftp://api.example.test")]
    [InlineData("api.example.test")]
    public void PlainHttpOrAnotherSchemeIsAnArgumentError(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => Make(baseUrl));
    }

    [Theory]
    [InlineData("https://api.example.test")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://LOCALHOST")]
    [InlineData("http://127.0.0.1:9000/")]
    [InlineData("http://[::1]:9000")]
    public void HttpsOrLoopbackHttpIsAccepted(string baseUrl)
    {
        using var dn = Make(baseUrl);
        Assert.Equal(baseUrl.TrimEnd('/'), dn.BaseUrl);
    }

    [Fact]
    public void PlainHttpFromTheEnvironmentIsRefusedToo()
    {
        var saved = Environment.GetEnvironmentVariable("DECISIONNODE_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("DECISIONNODE_BASE_URL", "http://api.example.test");
            Assert.Throws<ArgumentException>(() => Make());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DECISIONNODE_BASE_URL", saved);
        }
    }

    [Fact]
    public void TimeoutMustBeAboveZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(timeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(timeout: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(timeout: System.Threading.Timeout.InfiniteTimeSpan));
        using var ok = Make(timeout: TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void MaxRetriesMustBeZeroOrMore()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(maxRetries: -1));
        using var ok = Make(maxRetries: 0);
    }

    [Fact]
    public void AnHttpClientThatFollowsRedirectsIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient()));
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(new HttpClientHandler())));
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(new SocketsHttpHandler())));
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(new PassThrough { InnerHandler = new SocketsHttpHandler() })));
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(new PassThrough { InnerHandler = new SocketsHttpHandler() }), neverResend: true));
    }

    [Fact]
    public void AnHttpClientThatNeverFollowsRedirectsIsAccepted()
    {
        using var a = Make(http: new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }));
        using var b = Make(http: new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }));
        using var c = Make(http: new HttpClient(new PassThrough { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } }), neverResend: true);
    }

    [Fact]
    public void AnHttpClientWhoseTerminalHandlerIsUnknownIsRefused()
    {
        // A handler the client cannot inspect counts as one that follows redirects.
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(new OwnTerminal())));
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(new PassThrough { InnerHandler = new OwnTerminal() }), neverResend: true));
    }

    [Fact]
    public void ADelegatingHandlerWithoutAnInnerHandlerIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(new PassThrough()), neverResend: true));
    }

    [Fact]
    public void AHandlerThatOnlyCopiesTheWinHttpHandlerNameIsRefused()
    {
        // The stand-in has the real type's full name and properties but not its assembly.
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(new System.Net.Http.WinHttpHandler())));
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(new System.Net.Http.WinHttpHandler { AutomaticRedirection = false })));
    }

    [Fact]
    public void AnHttpClientThatReplacesTheCertificateCheckIsRefusedForHttps()
    {
        var sockets = new SocketsHttpHandler { AllowAutoRedirect = false };
        sockets.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(sockets)));

        var client = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(client)));
        Assert.Throws<ArgumentException>(() => Make(http: new HttpClient(new PassThrough { InnerHandler = client }), neverResend: true));
    }

    [Fact]
    public void ACertificateCallbackIsAllowedForLoopbackHttp()
    {
        var client = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var dn = Make(baseUrl: "http://localhost:8080", http: new HttpClient(client));
    }

    [Fact]
    public async Task AFollowedRedirectIsAnErrorEvenFromADelegatingHandler()
    {
        // A delegating handler that re-sends to another URL anyway: its answer is never used.
        var stub = new StubHandler((r, _) =>
        {
            var response = Http.Ok();
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://elsewhere.example.test/v1/decide");
            return response;
        });
        using var dn = new DecisionNodeClient(new DecisionNodeClientOptions { ApiKey = "k", BaseUrl = "https://api.example.test", HttpClient = new HttpClient(stub), DelegatingHandlersNeverResendRequests = true, MaxRetries = 0 });
        var e = await Assert.ThrowsAsync<DecisionNodeError>(() => dn.DecideAsync("s", Http.OneTruth()));
        Assert.Null(e.Status);
    }

    [Fact]
    public async Task TheTimeoutCoversTheWholeBody()
    {
        // The headers arrive at once; the body never finishes within the timeout.
        var stub = new StubHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new SlowContent(TimeSpan.FromSeconds(5)) });
        using var dn = new DecisionNodeClient(new DecisionNodeClientOptions
        {
            ApiKey = "k",
            BaseUrl = "https://api.example.test",
            HttpClient = new HttpClient(stub),
            DelegatingHandlersNeverResendRequests = true,
            Timeout = TimeSpan.FromMilliseconds(200),
            MaxRetries = 0,
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutError>(() => dn.DecideAsync("s", Http.OneTruth()));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
    }

    private sealed class PassThrough : DelegatingHandler
    {
    }

    private sealed class OwnTerminal : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Http.Ok());
    }

    private sealed class SlowContent : HttpContent
    {
        private readonly TimeSpan _delay;

        public SlowContent(TimeSpan delay) => _delay = delay;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync("{\"answers\":"u8.ToArray(), cancellationToken);
            await Task.Delay(_delay, cancellationToken);
            await stream.WriteAsync("{}}"u8.ToArray(), cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
