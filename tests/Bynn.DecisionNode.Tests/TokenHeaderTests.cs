using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

public class TokenHeaderTests
{
    private const string ImageHeader = "x-decisionnode-image-tokens";
    private const string VideoHeader = "x-decisionnode-video-tokens";
    private const string Body = """{"model":"decisionnode-flash","answers":{"q":{"type":"truth","truth":0.9}},"usage":{"input_tokens":1400,"output_tokens":0}}""";

    private static async Task<Decision> Decide(params (string, string)[] headers)
    {
        using var dn = Client(new StubHandler((_, _) => Ok(Body, headers)));
        var d = await dn.DecideAsync("s", OneTruth());
        Assert.Equal(1400, d.Usage.InputTokens); // never changed by the shares
        Assert.Null(d.Raw["usage"]!["image_tokens"]);
        return d;
    }

    [Fact]
    public async Task BothHeadersAreRead()
    {
        var d = await Decide((ImageHeader, "1024"), (VideoHeader, "300"));
        Assert.Equal(1024, d.Usage.ImageTokens);
        Assert.Equal(300, d.Usage.VideoTokens);
    }

    [Fact]
    public async Task AbsentHeadersReadAsNull()
    {
        var d = await Decide();
        Assert.Null(d.Usage.ImageTokens);
        Assert.Null(d.Usage.VideoTokens);
    }

    [Theory]
    [InlineData(ImageHeader)]
    [InlineData(VideoHeader)]
    public async Task EachHeaderIsReadOnItsOwn(string name)
    {
        var d = await Decide((name, "512"));
        var (mine, other) = name == ImageHeader ? (d.Usage.ImageTokens, d.Usage.VideoTokens) : (d.Usage.VideoTokens, d.Usage.ImageTokens);
        Assert.Equal(512, mine);
        Assert.Null(other);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("512", 512L)]
    [InlineData("  77 ", 77L)]
    [InlineData("2147483647", 2147483647L)]
    [InlineData("0002147483647", 2147483647L)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("-1", null)]
    [InlineData("-0", null)]
    [InlineData("+5", null)]
    [InlineData("12.0", null)]
    [InlineData("1e3", null)]
    [InlineData("abc", null)]
    [InlineData("12 3", null)]
    [InlineData("0x10", null)]
    [InlineData("2147483648", null)]
    [InlineData("99999999999999999999999", null)]
    public void AHeaderValueIsAWholeNumberUpToTheLimit(string value, long? expected)
    {
        Assert.Equal(expected, Decision.TokenHeader(value));
    }

    [Theory]
    [InlineData(ImageHeader, "")]
    [InlineData(ImageHeader, "-1")]
    [InlineData(ImageHeader, "abc")]
    [InlineData(ImageHeader, "2147483648")]
    [InlineData(VideoHeader, "")]
    [InlineData(VideoHeader, "-1")]
    [InlineData(VideoHeader, "abc")]
    [InlineData(VideoHeader, "2147483648")]
    public async Task ABadHeaderReadsAsNullAndNeverFailsTheDecision(string name, string value)
    {
        var d = await Decide((name, value));
        Assert.Null(d.Usage.ImageTokens);
        Assert.Null(d.Usage.VideoTokens);
        Assert.Equal(0.9, d.Truth("q").Truth);
    }

    [Theory]
    [InlineData(ImageHeader, " 42 ")]
    [InlineData(VideoHeader, " 42 ")]
    public async Task SpacesAroundAHeaderAreTrimmed(string name, string value)
    {
        var d = await Decide((name, value));
        Assert.Equal(42, name == ImageHeader ? d.Usage.ImageTokens : d.Usage.VideoTokens);
    }

    [Fact]
    public async Task ABatchResultReadsNull()
    {
        var file = """{"custom_id":"a","status":200,"body":{"answers":{"q":{"type":"truth","truth":0.88}},"usage":{"input_tokens":1400,"output_tokens":0}},"headers":{"x-request-id":"req_1"}}""" + "\n";
        using var dn = Client(new StubHandler((_, _) => Json(200, file)));
        var results = new List<BatchResult>();
        await foreach (var r in dn.Batches.ResultsAsync("batch_01J9ZB8Q3T"))
        {
            results.Add(r);
        }
        var d = Assert.Single(results).Decision!;
        Assert.Equal("req_1", d.RequestId);
        Assert.Null(d.Usage.ImageTokens);
        Assert.Null(d.Usage.VideoTokens);
    }

    [Fact]
    public void TheSpecVersionIsOnePointFive()
    {
        Assert.Equal("1.5", DecisionNodeClient.SpecVersion);
        Assert.Equal("0.5.1", DecisionNodeClient.Version);
    }
}
