using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>The fallback option and the answer of a model that stood in for the one asked for.</summary>
public class FallbackTests
{
    [Fact]
    public async Task FallbackIsSentOnlyWhenGiven()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("s", OneTruth());
        await dn.DecideAsync("s", OneTruth(), fallback: false);
        await dn.DecideAsync("s", OneTruth(), fallback: true);
        var sent = stub.Requests.ToList();
        Assert.False(sent[0].Json!.AsObject().ContainsKey("fallback"));
        Assert.Equal("false", sent[1].Json!["fallback"]!.ToJsonString());
        Assert.Equal("true", sent[2].Json!["fallback"]!.ToJsonString());
    }

    [Fact]
    public async Task EveryCallTakesFallback()
    {
        var stub = new StubHandler((r, _) => r.Uri.AbsolutePath == "/v1/decide"
            ? Ok("""{"answers":{"q":{"type":"truth","truth":0.5},"check":{"type":"truth","truth":0.5},"classify":{"type":"choice","choice":"a","confidence":0.1,"probabilities":{"a":1.0}},"score":{"type":"score","score":0.0,"confidence":1.0,"probabilities":{"0":1.0},"legend":{"0":"x"}},"number":{"type":"number","number":1,"expected":1.0,"confidence":1.0,"probabilities":{"1":1.0}}}}""")
            : Json(201, """{"id":"batch_01J9ZB8Q3T","state":"draft","counters":{}}"""));
        using var dn = Client(stub);
        await dn.CheckAsync("s", "?", fallback: false);
        await dn.ClassifyAsync("s", "?", new[] { "a" }, fallback: false);
        await dn.ScoreAsync("s", "?", new object?[] { "x" }, fallback: false);
        await dn.NumberAsync("s", "?", fallback: false);
        await dn.GateAsync("deploy", fallback: false);
        await dn.DecideAsync(new DecideRequest { Questions = OneTruth(), Fallback = false });
        await dn.DecideManyAsync(new[] { new DecideRequest { Questions = OneTruth(), Fallback = false } });
        await dn.Batches.CreateAsync(new[] { new BatchRequest { CustomId = "a", Questions = OneTruth(), Fallback = false } });
        var sent = stub.Requests.ToList();
        Assert.Equal(8, sent.Count);
        foreach (var r in sent.Take(7))
        {
            Assert.Equal("false", r.Json!["fallback"]!.ToJsonString());
        }
        Assert.Equal(
            """{"custom_id":"a","model":"decisionnode-moe","state":"","questions":{"q":{"type":"truth","instructions":"?"}},"fallback":false}""",
            sent[7].Body!.TrimEnd('\n'));
    }

    [Fact]
    public async Task AFallbackAnswerNamesBothModels()
    {
        const string body = """{"model":"decisionnode-flash-0.9.0-rc1","answers":{"q":{"type":"truth","truth":0.5}},"usage":{"input_tokens":9,"output_tokens":0}}""";
        using var dn = Client(new StubHandler((_, _) => Ok(body,
            ("x-decisionnode-model", "decisionnode-flash-0.9.0-rc1"),
            ("x-decisionnode-fallback", "decisionnode-flash-0.9.0-rc1"),
            ("x-decisionnode-requested-model", "decisionnode-moe-0.9.0-rc1"))));
        var d = await dn.DecideAsync("s", OneTruth(), model: "decisionnode-moe");
        Assert.Equal("decisionnode-flash-0.9.0-rc1", d.Model);
        Assert.Equal(new Fallback("decisionnode-flash-0.9.0-rc1", "decisionnode-moe-0.9.0-rc1"), d.Fallback);
    }

    [Fact]
    public async Task AnOrdinaryAnswerHasNoFallback()
    {
        using var dn = Client(new StubHandler((_, _) => Ok()));
        var d = await dn.DecideAsync("s", OneTruth());
        Assert.Null(d.Fallback);
    }

    [Fact]
    public async Task TheOptedOutOverloadIsRetriedLikeAny529()
    {
        var stub = new StubHandler((_, _) => Json(529, """{"detail":{"error_type":"overloaded_error","message":"Overloaded."}}""", ("retry-after-ms", "5")));
        using var dn = Client(stub);
        dn.Delay = (_, _) => Task.CompletedTask;
        await Assert.ThrowsAsync<OverloadedError>(() => dn.DecideAsync("s", OneTruth(), model: "decisionnode-moe", fallback: false));
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public void CheckRequestTakesFallback()
    {
        using var dn = Client(new StubHandler((_, _) => Ok()));
        dn.CheckRequest(new DecideRequest { Questions = OneTruth(), Fallback = false });
    }
}
