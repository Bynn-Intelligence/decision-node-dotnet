using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Boosters, their evidence and usage, and fine-tuned model names.</summary>
public class SpecOneThreeTests
{
    private const string BoostedBody = """
        {"model":"decisionnode-1.0","answers":{"adult":{"type":"truth","truth":0.97}},
         "evidence":{
           "age":{"version":"age-v3","status":"ok","latency_ms":212,"billed_calls":1,"results":[
             {"image":"a1b2","age_years":27,"low":22,"high":33,"confidence":0.81,"faces":1},
             {"image":"c3d4","faces":0}]},
           "ai_generated":{"version":"ai_generated-v2","status":"ok","latency_ms":340,"results":[
             {"image":"c3d4","generated":0.93,"confidence":0.86,"method":"inference","top_generator":{"name":"gen-a","probability":0.71},"c2pa_signer":null},
             {"image":"a1b2","generated":0.99,"confidence":1.0,"method":"c2pa","top_generator":null,"c2pa_signer":"Example Signer"},
             {"image":"e5f6","generated":0.08,"confidence":0.8200000000000001,"method":"inference","top_generator":null,"c2pa_signer":null,"c2pa":"untrusted","region":{"x":4}}]}},
         "usage":{"input_tokens":341,"output_tokens":0,"booster_tokens":72,"boosters":3}}
        """;

    private static Image[] TwoPictures() => new[]
    {
        Image.FromUrl("a1b2", "https://files.example.com/a1b2.jpg"),
        Image.FromUrl("c3d4", "https://files.example.com/c3d4.jpg"),
    };

    [Fact]
    public void TheSpecVersionIsOnePointThree()
    {
        Assert.Equal("1.3", DecisionNodeClient.SpecVersion);
        Assert.Equal("0.3.0", DecisionNodeClient.Version);
    }

    [Fact]
    public async Task BoostersAreSentInTheirWireFormsInOrder()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("s", OneTruth(), TwoPictures(), boosters: new Booster[]
        {
            "age",
            Booster.Of("ai_generated", images: new[] { "c3d4" }, require: true),
            Booster.Of("later", options: new { level = 2 }),
            Booster.Of("bare"),
            new JsonObject { ["name"] = "raw", ["require"] = false },
            Booster.FromJson(JsonValue.Create("named")!),
        });
        var body = Assert.Single(stub.Requests).Json!;
        Assert.Equal(
            """["age",{"name":"ai_generated","images":["c3d4"],"require":true},{"name":"later","options":{"level":2}},"bare",{"name":"raw","require":false},"named"]""",
            body["boosters"]!.ToJsonString());
    }

    [Fact]
    public async Task NoBoostersKeyWhenNoneOrEmpty()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("s", OneTruth());
        await dn.DecideAsync("s", OneTruth(), boosters: Array.Empty<Booster>());
        await dn.DecideAsync(new DecideRequest { State = "s", Questions = OneTruth(), Boosters = new List<Booster>() });
        Assert.All(stub.Requests, r => Assert.Equal(
            """{"model":"decisionnode-latest","state":"s","questions":{"q":{"type":"truth","instructions":"?"}}}""",
            r.Body));
    }

    [Fact]
    public async Task HelpersDecideManyAndBatchLinesCarryBoosters()
    {
        var stub = new StubHandler((r, _) => r.Uri.AbsolutePath == "/v1/decide"
            ? Ok("""{"answers":{"check":{"type":"truth","truth":0.5},"classify":{"type":"choice","choice":"a","confidence":0.1,"probabilities":{"a":1.0}},"score":{"type":"score","score":0.0,"confidence":1.0,"probabilities":{"0":1.0},"legend":{"0":"x"}},"number":{"type":"number","number":1,"expected":1.0,"confidence":1.0,"probabilities":{"1":1.0}}}}""")
            : Json(201, """{"id":"batch_01J9ZB8Q3T","state":"draft","counters":{}}"""));
        using var dn = Client(stub);
        var boosters = new Booster[] { "age" };
        await dn.CheckAsync("s", "?", boosters: boosters);
        await dn.ClassifyAsync("s", "?", new[] { "a" }, boosters: boosters);
        await dn.ScoreAsync("s", "?", new object?[] { "x" }, boosters: boosters);
        await dn.NumberAsync("s", "?", boosters: boosters);
        await dn.GateAsync("deploy", boosters: boosters);
        await dn.DecideManyAsync(new[] { new DecideRequest { Questions = OneTruth(), Boosters = boosters } });
        await dn.Batches.CreateAsync(new[] { new BatchRequest { CustomId = "a", Questions = OneTruth(), Boosters = boosters } });
        var sent = stub.Requests.ToList();
        Assert.Equal(7, sent.Count);
        foreach (var r in sent.Take(6))
        {
            Assert.Equal("""["age"]""", r.Json!["boosters"]!.ToJsonString());
        }
        Assert.Equal(
            """{"custom_id":"a","model":"decisionnode-latest","state":"","questions":{"q":{"type":"truth","instructions":"?"}},"boosters":["age"]}""",
            sent[6].Body!.TrimEnd('\n'));
    }

    [Fact]
    public void NothingAboutBoostersIsCheckedLocally()
    {
        using var dn = Client(new StubHandler((_, _) => Ok()));
        dn.CheckRequest(new DecideRequest
        {
            Questions = OneTruth(),
            Boosters = new Booster[] { "age", "age", "nope", "x", "y", Booster.Of("age", options: new { unknown = true }) },
        });
    }

    [Fact]
    public void TheNestingLimitCountsTheBoostersOptions()
    {
        using var dn = Client(new StubHandler((_, _) => Ok()));
        // The body, the boosters list and the booster object take three levels.
        JsonNode Nest(int depth)
        {
            JsonNode node = new JsonObject();
            for (var i = 1; i < depth; i++)
            {
                node = new JsonArray(node);
            }
            return node;
        }
        dn.CheckRequest(new DecideRequest { Questions = OneTruth(), Boosters = new Booster[] { Booster.Of("age", options: Nest(125)) } });
        var error = Assert.Throws<BadRequestError>(() =>
            dn.CheckRequest(new DecideRequest { Questions = OneTruth(), Boosters = new Booster[] { Booster.Of("age", options: Nest(126)) } }));
        Assert.Equal("api_usage_error", error.ErrorType);
        Assert.Equal("Invalid request.", error.Message);
        Assert.Null(error.Status);
    }

    [Fact]
    public async Task EvidenceAndUsageAreRead()
    {
        using var dn = Client(new StubHandler((_, _) => Ok(BoostedBody)));
        var d = await dn.DecideAsync("s", OneTruth(), TwoPictures(), boosters: new Booster[] { "age", "ai_generated" });
        Assert.Equal(new[] { "age", "ai_generated" }, d.Evidence!.Keys);
        var age = d.Evidence["age"];
        Assert.Equal("age-v3", age.Version);
        Assert.Equal("ok", age.Status);
        Assert.Equal(212, age.LatencyMs);
        Assert.Equal(1, age.BilledCalls);
        Assert.Null(d.Evidence["ai_generated"].BilledCalls);
        var first = Assert.IsType<AgeReading>(age.Results[0]);
        Assert.Equal(("a1b2", 1L, 27L, 22L, 33L, 0.81), (first.Image, first.Faces, first.AgeYears, first.Low, first.High, first.Confidence));
        var noFace = Assert.IsType<AgeReading>(age.Results[1]);
        Assert.Equal(0, noFace.Faces);
        Assert.Null(noFace.AgeYears);
        Assert.Null(noFace.Low);
        Assert.Null(noFace.High);
        Assert.Null(noFace.Confidence);
        var inference = Assert.IsType<AiGeneratedReading>(d.Evidence["ai_generated"].Results[0]);
        Assert.Equal(("c3d4", 0.93, 0.86, "inference"), (inference.Image, inference.Generated, inference.Confidence, inference.Method));
        Assert.Equal(new TopGenerator("gen-a", 0.71), inference.TopGenerator);
        Assert.Null(inference.C2paSigner);
        var c2pa = Assert.IsType<AiGeneratedReading>(d.Evidence["ai_generated"].Results[1]);
        Assert.Equal("c2pa", c2pa.Method);
        Assert.Equal("Example Signer", c2pa.C2paSigner);
        Assert.Null(c2pa.TopGenerator);
        Assert.Null(c2pa.C2pa);
        var photo = Assert.IsType<AiGeneratedReading>(d.Evidence["ai_generated"].Results[2]);
        Assert.Null(photo.TopGenerator);
        Assert.Equal("untrusted", photo.C2pa);
        Assert.Equal(0.8200000000000001, photo.Confidence);
        Assert.Null(inference.C2pa);
        Assert.Equal("ai_generated", c2pa.Booster);
        Assert.Equal(72, d.Usage.BoosterTokens);
        Assert.Equal(3, d.Usage.Boosters);
        Assert.Equal(341, d.Usage.InputTokens);
    }

    [Fact]
    public async Task TimedOutSkippedUnknownAndMalformedReadings()
    {
        const string body = """
            {"answers":{},"evidence":{
              "age":{"version":"age-v3","status":"timeout","latency_ms":800,"results":[]},
              "ai_generated":{"version":"ai_generated-v2","status":"skipped","latency_ms":0,"results":[]},
              "depth":{"version":"depth-v1","status":"ok","latency_ms":10,"results":[{"image":"a","metres":3.5}]},
              "later":{"version":"later-v1","status":"queued","results":[{"image":"a"}]}},
             "usage":{"input_tokens":5,"output_tokens":0,"booster_tokens":0,"boosters":0}}
            """;
        using var dn = Client(new StubHandler((_, _) => Ok(body)));
        var d = await dn.DecideAsync("s", OneTruth());
        Assert.Equal("timeout", d.Evidence!["age"].Status);
        Assert.Empty(d.Evidence["age"].Results);
        Assert.Equal("skipped", d.Evidence["ai_generated"].Status);
        Assert.Empty(d.Evidence["ai_generated"].Results);
        var unknown = Assert.IsType<UnknownReading>(Assert.Single(d.Evidence["depth"].Results));
        Assert.Equal("depth", unknown.Booster);
        Assert.Equal("a", unknown.Image);
        Assert.Equal(3.5, (double)unknown.Raw!["metres"]!);
        Assert.Equal("queued", d.Evidence["later"].Status);
        Assert.Null(d.Evidence["later"].LatencyMs);
        Assert.Equal(0, d.Usage.Boosters);

        const string malformed = """
            {"answers":{},"evidence":{
              "age":{"version":"age-v3","status":"ok","latency_ms":1,"results":[
                {"image":"a","faces":1,"age_years":"27","low":22,"high":33,"confidence":0.8},
                {"image":"b","faces":2},
                {"faces":0},
                7]},
              "ai_generated":{"version":"ai_generated-v2","status":"ok","latency_ms":1,"results":[
                {"image":"a","generated":0.5,"confidence":0.5,"method":"inference","top_generator":"x","c2pa_signer":null},
                {"image":"b","generated":true,"confidence":0.5,"method":"inference"},
                {"image":"c","generated":0.5,"confidence":0.5,"method":"inference","c2pa_signer":3},
                {"image":"d","generated":0.5,"confidence":0.5,"method":"inference","top_generator":null,"c2pa_signer":null,"c2pa":true},
                {"image":"g","generated":0.5,"confidence":0.5,"method":"inference","c2pa_signer":null},
                {"image":"e","generated":0.5,"confidence":0.5,"method":"inference","top_generator":{"name":"gen-a","probability":0.1}},
                {"image":"f","generated":0.5,"confidence":0.5,"method":"other","top_generator":{"name":"gen-a","probability":0.1},"c2pa_signer":null}]}}}
            """;
        using var dn2 = Client(new StubHandler((_, _) => Ok(malformed)));
        var m = await dn2.DecideAsync("s", OneTruth());
        Assert.All(m.Evidence!["age"].Results, r => Assert.IsType<UnknownReading>(r));
        Assert.Equal(7, (int)m.Evidence["age"].Results[3].Raw!);
        var readings = m.Evidence["ai_generated"].Results;
        Assert.All(readings.Take(6), r => Assert.IsType<UnknownReading>(r));
        var other = Assert.IsType<AiGeneratedReading>(readings[6]);
        Assert.Equal("other", other.Method);
        Assert.Null(other.C2paSigner);
    }

    [Fact]
    public async Task NoEvidenceAndNoBoosterUsageWithoutBoosters()
    {
        using var dn = Client(new StubHandler((_, _) => Ok()));
        var d = await dn.DecideAsync("s", OneTruth());
        Assert.Null(d.Evidence);
        Assert.Null(d.Usage.BoosterTokens);
        Assert.Null(d.Usage.Boosters);
    }

    [Fact]
    public void TheBoostersHeaderIsParsedInOrder()
    {
        var map = BoosterStatus.ParseHeader("ai_generated=timeout:800, age=ok:212,broken,noms=error,odd=ok:x");
        Assert.Equal(new[] { "ai_generated", "age", "noms", "odd" }, map.Keys);
        Assert.Equal(new BoosterStatus("timeout", 800), map["ai_generated"]);
        Assert.Equal(new BoosterStatus("ok", 212), map["age"]);
        Assert.Equal(new BoosterStatus("error", null), map["noms"]);
        Assert.Equal(new BoosterStatus("ok", null), map["odd"]);
        Assert.Empty(BoosterStatus.ParseHeader(null));
        Assert.Empty(BoosterStatus.ParseHeader(""));
    }

    [Theory]
    [InlineData(400, "booster_unknown", typeof(BadRequestError))]
    [InlineData(400, "booster_input_mismatch", typeof(BadRequestError))]
    [InlineData(400, "api_usage_error", typeof(BadRequestError))]
    [InlineData(403, "booster_not_enabled", typeof(PermissionDeniedError))]
    [InlineData(403, "workspace_suspended", typeof(PermissionDeniedError))]
    [InlineData(403, null, typeof(PermissionDeniedError))]
    [InlineData(403, "refusal_error", typeof(RefusalError))]
    [InlineData(529, "booster_unavailable", typeof(OverloadedError))]
    public async Task BoosterErrorsMapToTheirClasses(int status, string? errorType, Type expected)
    {
        var inner = new JsonObject { ["message"] = "no" };
        if (errorType is not null)
        {
            inner["error_type"] = errorType;
        }
        var detail = new JsonObject { ["detail"] = inner }.ToJsonString();
        var stub = new StubHandler((_, _) => Json(status, detail, ("x-decisionnode-boosters", "age=ok:212,ai_generated=error:15"), ("x-request-id", "req_1")));
        using var dn = Client(stub, maxRetries: 0);
        var error = await Assert.ThrowsAnyAsync<DecisionNodeError>(() => dn.DecideAsync("s", OneTruth(), boosters: new Booster[] { "age", "ai_generated" }));
        Assert.IsType(expected, error);
        Assert.Equal(errorType, error.ErrorType);
        Assert.Equal(new[] { "age", "ai_generated" }, error.Boosters.Keys);
        Assert.Equal(new BoosterStatus("error", 15), error.Boosters["ai_generated"]);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task BoosterUnavailableIsRetriedThenRaisedWithTheHeader()
    {
        var stub = new StubHandler((_, _) => Json(529, """{"detail":{"error_type":"booster_unavailable","message":"The booster 'age' did not answer in time."}}""",
            ("x-decisionnode-boosters", "age=timeout:800"), ("retry-after-ms", "5")));
        using var dn = Client(stub);
        var waits = new List<TimeSpan>();
        dn.Delay = (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsAsync<OverloadedError>(() => dn.DecideAsync("s", OneTruth(), boosters: new[] { Booster.Of("age", require: true) }));
        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal(2, waits.Count);
        Assert.Equal("booster_unavailable", error.ErrorType);
        Assert.Equal(new BoosterStatus("timeout", 800), Assert.Single(error.Boosters).Value);
    }

    [Fact]
    public async Task AnErrorWithoutTheHeaderHasNoBoosters()
    {
        using var dn = Client(new StubHandler((_, _) => Json(400, """{"detail":{"error_type":"api_usage_error","message":"Unknown model: decisionnode-1.0-flash@acme/fraud"}}""")));
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync("s", OneTruth(), model: "decisionnode-1.0-flash@acme/fraud"));
        Assert.Empty(error.Boosters);
        Assert.Equal("Unknown model: decisionnode-1.0-flash@acme/fraud", error.Message);
    }

    [Fact]
    public async Task AFineTunedModelNameIsSentAsGivenAndTheAnswerNamesThePinnedVersion()
    {
        var stub = new StubHandler((_, _) => Ok("""{"model":"decisionnode-1.0-flash@acme/fraud:3","answers":{"q":{"type":"truth","truth":0.5}}}"""));
        using var dn = Client(stub);
        var d = await dn.DecideAsync("s", OneTruth(), model: "decisionnode-1.0-flash@acme/fraud");
        Assert.Equal("decisionnode-1.0-flash@acme/fraud", (string?)Assert.Single(stub.Requests).Json!["model"]);
        Assert.Equal("decisionnode-1.0-flash@acme/fraud:3", d.Model);
    }

    [Fact]
    public async Task ModelsListFineTunedEntries()
    {
        const string body = """
            {"models":[
              {"name":"decisionnode-1.0-flash","description":"Fast","release_date":"2026-10-01","version":"decisionnode-1.0-flash","max_images":16},
              {"name":"decisionnode-1.0-flash@acme/fraud:3","description":"Claims fraud","release_date":"2026-10-09","version":"decisionnode-1.0-flash@acme/fraud:3","fine_tuned":true,"base":"decisionnode-1.0-flash","adapter_version":3,"status":"deployed"},
              {"name":"decisionnode-1.0-flash@acme/fraud:4","description":"Claims fraud","release_date":"2026-10-10","fine_tuned":true,"base":"decisionnode-1.0-flash","adapter_version":4,"status":"ready"}]}
            """;
        using var dn = Client(new StubHandler((_, _) => Ok(body)));
        var models = await dn.ModelsAsync();
        var catalogue = models[0];
        Assert.Null(catalogue.FineTuned);
        Assert.Null(catalogue.Base);
        Assert.Null(catalogue.AdapterVersion);
        Assert.Null(catalogue.Status);
        var deployed = models[1];
        Assert.True(deployed.FineTuned);
        Assert.Equal("decisionnode-1.0-flash", deployed.Base);
        Assert.Equal(3, deployed.AdapterVersion);
        Assert.Equal("deployed", deployed.Status);
        Assert.Equal("decisionnode-1.0-flash@acme/fraud:3", (string?)deployed.Limits["version"]);
        Assert.Equal("ready", models[2].Status);
        Assert.Equal(4, models[2].AdapterVersion);
    }

    [Fact]
    public void BoosterNamesAndForms()
    {
        Booster named = "age";
        Assert.Equal("age", named.Name);
        Assert.Equal("\"age\"", named.ToString());
        Assert.Equal("ai_generated", Booster.Of(Booster.AiGenerated, require: true).Name);
        Assert.Equal("""{"name":"x"}""", Booster.FromObject(new Dictionary<string, object> { ["name"] = "x" }).ToJson().ToJsonString());
        Assert.Equal("x", Booster.FromObject(new Dictionary<string, object> { ["name"] = "x" }).Name);
        Assert.Equal("\"bare\"", Booster.Of("bare").ToString());
        Assert.Equal("bare", Booster.Of("bare").Name);
        Assert.Throws<ArgumentNullException>(() => Booster.Of(null!));
    }
}
