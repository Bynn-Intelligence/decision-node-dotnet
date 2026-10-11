using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>A decision naming the ocr booster: 30 s an attempt by default, never retried after a timeout.</summary>
public class OcrTimeoutTests
{
    private static StubHandler Slow(TimeSpan delay) => new(async (_, _, ct) =>
    {
        await Task.Delay(delay, ct);
        return Ok();
    });

    private static bool NamesOcr(Recorded r) => r.Body!.Contains("\"ocr\"", StringComparison.Ordinal);

    [Fact]
    public async Task AnOcrRequestIsNeverRetriedAfterATimeout()
    {
        var stub = Slow(TimeSpan.FromSeconds(5));
        using var dn = Client(stub, timeout: TimeSpan.FromMilliseconds(300));
        var error = await Assert.ThrowsAsync<TimeoutError>(() => dn.DecideAsync("s", OneTruth(), boosters: new Booster[] { "ocr" }));
        Assert.Single(stub.Requests);
        // A timeout the caller set is used as given, even below 30 s.
        Assert.Contains("0.3 s", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheObjectFormCountsAndSoDoTheHelpers()
    {
        var stub = Slow(TimeSpan.FromSeconds(5));
        using var dn = Client(stub, timeout: TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<TimeoutError>(() => dn.CheckAsync("s", "?", boosters: new[] { Booster.Of(Booster.Ocr, options: new { prompt = "the total" }) }));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ARequestWithoutOcrIsStillRetriedAfterATimeout()
    {
        var stub = Slow(TimeSpan.FromSeconds(5));
        using var dn = Client(stub, maxRetries: 1, timeout: TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<TimeoutError>(() => dn.DecideAsync("s", OneTruth(), boosters: new Booster[] { "age" }));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task AnOcrRequestKeepsItsOtherRetries()
    {
        var stub = new StubHandler((_, i) => i == 0 ? Json(503, """{"detail":{"error_type":"api_error","message":""}}""") : Ok());
        using var dn = Client(stub, maxRetries: 1);
        var d = await dn.DecideAsync("s", OneTruth(), boosters: new Booster[] { "ocr" });
        Assert.Equal(0.5, d.Truth("q").Truth);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task InDecideManyOnlyTheOcrItemGoesUnretried()
    {
        var stub = Slow(TimeSpan.FromSeconds(5));
        using var dn = Client(stub, maxRetries: 1, timeout: TimeSpan.FromMilliseconds(300));
        var results = await dn.DecideManyAsync(new[]
        {
            new DecideRequest { State = "a", Questions = OneTruth(), Boosters = new Booster[] { "ocr" } },
            new DecideRequest { State = "b", Questions = OneTruth() },
        });
        Assert.IsType<TimeoutError>(results[0].Error);
        Assert.IsType<TimeoutError>(results[1].Error);
        Assert.Equal(1, stub.Requests.Count(NamesOcr));
        Assert.Equal(2, stub.Requests.Count(r => !NamesOcr(r)));
    }

    [Fact]
    public async Task WithoutATimeoutSetAnOcrAttemptWaits30Seconds()
    {
        // Over the 10 s default, under the 30 s an ocr attempt is allowed.
        var stub = Slow(TimeSpan.FromSeconds(11));
        using var dn = Client(stub);
        var d = await dn.DecideAsync("s", OneTruth(), boosters: new Booster[] { "ocr" });
        Assert.Equal(0.5, d.Truth("q").Truth);
        Assert.Single(stub.Requests);
    }
}
