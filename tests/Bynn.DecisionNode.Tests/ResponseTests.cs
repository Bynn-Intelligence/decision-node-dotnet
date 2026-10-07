using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

public class ResponseTests
{
    private static async Task<Decision> Decide(string body, params (string, string)[] headers)
    {
        using var dn = Client(new StubHandler((_, _) => Ok(body, headers)));
        return await dn.DecideAsync("s", OneTruth());
    }

    [Fact]
    public async Task ReadsEveryFieldOfADecision()
    {
        var d = await Decide(
            """{"model":"decisionnode-1.0","answers":{"route":{"type":"choice","choice":"billing","confidence":0.73,"probabilities":{"account":0.12,"billing":0.8200000000000001,"bug":0.060000000000000005}},"urgency":{"type":"score","score":2.31,"confidence":0.31,"legend":{"0":"routine","1":"today","2":"urgent","3":"critical"},"probabilities":{"0":0.01,"1":0.09,"2":0.48,"3":0.42}},"escalate":{"type":"truth","truth":0.94}},"usage":{"input_tokens":62,"output_tokens":0}}""",
            ("x-request-id", "req_7"), ("x-decisionnode-latency-ms", "23"), ("x-decisionnode-safety", "harm=0.031, self_harm=0.004"));

        Assert.Equal("decisionnode-1.0", d.Model);
        Assert.Equal(new Usage(62, 0), d.Usage);
        Assert.Equal("req_7", d.RequestId);
        Assert.Equal(23, d.LatencyMs);

        var route = d.Choice("route");
        Assert.Equal("billing", route.Choice);
        Assert.Equal(0.8200000000000001, route.Probabilities["billing"]); // never rounded
        Assert.Equal(0.060000000000000005, route.Probabilities["bug"]);
        Assert.Equal(new[] { "account", "billing", "bug" }, route.Probabilities.Keys.ToArray());

        var urgency = d["urgency"].AsScore();
        Assert.Equal(2.31, urgency.Score);
        Assert.Equal(0.48, urgency.Probabilities[2]);
        Assert.Equal("critical", (string?)urgency.Legend[3]);

        Assert.Equal(0.94, d.Truth("escalate").Truth);
        Assert.True(d.Safety!.Checked);
        Assert.Equal(0.031, d.Safety.Harm);
    }

    [Fact]
    public async Task UnknownAnswerTypeIsKeptRaw()
    {
        var d = await Decide("""{"answers":{"future":{"type":"ranking","order":["b","a"]}}}""");
        var a = d["future"];
        Assert.IsType<UnknownAnswer>(a);
        Assert.Equal("ranking", a.Type);
        Assert.Equal("""["b","a"]""", a.Raw["order"]!.ToJsonString());
        Assert.Throws<InvalidCastException>(() => a.AsTruth());
    }

    [Fact]
    public async Task UnknownFieldsStayInRaw()
    {
        var d = await Decide("""{"answers":{"q":{"type":"truth","truth":1}},"cost_usd":0.0001}""");
        Assert.Equal(1.0, d.Truth("q").Truth);
        Assert.Equal(0.0001, (double)d.Raw["cost_usd"]!);
    }

    [Fact]
    public async Task MissingHeadersReadAsNull()
    {
        var d = await Decide(TruthBody);
        Assert.Null(d.LatencyMs);
        Assert.Null(d.Safety);
        Assert.Null(d.RequestId);
    }

    [Fact]
    public async Task AKnownTypeWithMissingOrWronglyTypedFieldsIsAnUnknownAnswer()
    {
        var d = await Decide("""{"answers":{"t":{"type":"truth"},"t2":{"type":"truth","truth":"0.9"},"c":{"type":"choice","choice":"a","confidence":0.5,"probabilities":{"a":"high"}},"s":{"type":"score","score":1,"confidence":0.5,"probabilities":{"0":0.5,"1":0.5},"legend":{"x":"low"}},"ok":{"type":"truth","truth":0.25}}}""");
        foreach (var id in new[] { "t", "t2", "c", "s" })
        {
            var a = Assert.IsType<UnknownAnswer>(d[id]);
            Assert.Same(((JsonObject)d.Raw["answers"]!)[id], a.Raw);
        }
        Assert.Equal("truth", d["t"].Type);
        Assert.Equal(0.25, d.Truth("ok").Truth);
    }

    [Fact]
    public async Task NonJsonSuccessIsAServerError()
    {
        using var dn = Client(new StubHandler((_, _) => Ok("<html>")));
        var e = await Assert.ThrowsAsync<ServerError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal(200, e.Status);
    }
}

public class SafetyTests
{
    [Fact]
    public void ParsesScoresInHeaderOrder()
    {
        var s = Safety.Parse("harm=0.010, self_harm=0.020")!;
        Assert.True(s.Checked);
        Assert.Equal(0.01, s.Harm);
        Assert.Equal(0.02, s.SelfHarm);
        Assert.Empty(s.Flagged);
        Assert.Null(s.Reason);
    }

    [Fact]
    public void FlaggedAndBlockedCategoriesAreListed()
    {
        Assert.Equal(new[] { "harm" }, Safety.Parse("harm=0.912;flagged, self_harm=0.004")!.Flagged);
        Assert.Equal(new[] { "harm", "self_harm" }, Safety.Parse("harm=0.96;blocked, self_harm=0.95;flagged")!.Flagged);
    }

    [Fact]
    public void UncheckedCarriesTheReason()
    {
        var s = Safety.Parse("unchecked;reason=\"no-model-question\"")!;
        Assert.False(s.Checked);
        Assert.Equal("no-model-question", s.Reason);
        Assert.Null(s.Harm);
    }

    [Fact]
    public void NoHeaderIsNoSafety()
    {
        Assert.Null(Safety.Parse(null));
    }
}
