using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

public class RetryTests
{
    private const string Overloaded = """{"detail":{"error_type":"overloaded_error","message":"Overloaded."}}""";

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(529)]
    [InlineData(599)]
    public async Task RetryableStatusIsRetriedThenSucceeds(int status)
    {
        var stub = new StubHandler((_, i) => i == 0 ? Json(status, Overloaded, ("retry-after-ms", "1")) : Ok(headers: ("x-request-id", "req_second")));
        using var dn = Client(stub);
        var d = await dn.DecideAsync("s", OneTruth());
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal("req_second", d.RequestId); // headers come from the final attempt
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task ClientErrorsAreNeverRetried(int status)
    {
        var stub = new StubHandler((_, _) => Json(status, """{"detail":{"error_type":"refusal_error"}}""", ("retry-after-ms", "1")));
        using var dn = Client(stub, maxRetries: 5);
        await Assert.ThrowsAnyAsync<DecisionNodeError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    [InlineData(4, 5)]
    public async Task MaxRetriesCountsRetriesAfterTheFirstAttempt(int maxRetries, int attempts)
    {
        var stub = new StubHandler((_, _) => Json(529, Overloaded, ("retry-after-ms", "1")));
        using var dn = Client(stub, maxRetries: maxRetries);
        await Assert.ThrowsAsync<OverloadedError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal(attempts, stub.Requests.Count);
    }

    [Fact]
    public async Task ConnectionErrorsAreRetried()
    {
        var stub = new StubHandler((_, i) => i < 2 ? throw new HttpRequestException("reset") : Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("s", OneTruth());
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task ConnectionErrorAfterLastRetry()
    {
        var stub = new StubHandler((Func<Recorded, int, HttpResponseMessage>)((_, _) => throw new HttpRequestException("reset")));
        using var dn = Client(stub, maxRetries: 1);
        await Assert.ThrowsAsync<ConnectionError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task SlowAttemptTimesOutAndIsRetried()
    {
        var stub = new StubHandler(async (_, i, ct) =>
        {
            if (i == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            return Ok();
        });
        using var dn = Client(stub, timeout: TimeSpan.FromMilliseconds(100));
        await dn.DecideAsync("s", OneTruth());
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task TimeoutErrorWhenEveryAttemptIsSlow()
    {
        var stub = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Ok();
        });
        using var dn = Client(stub, timeout: TimeSpan.FromMilliseconds(50), maxRetries: 0);
        var e = await Assert.ThrowsAsync<TimeoutError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Null(e.Status);
    }

    [Fact]
    public async Task CallerCancellationIsNotATimeout()
    {
        var stub = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Ok();
        });
        using var dn = Client(stub, timeout: TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dn.DecideAsync("s", OneTruth(), cancellationToken: cts.Token));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public void BackoffStartsAtHalfASecondAndIsCappedAtEight()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(DecisionNodeClient.Backoff(0).TotalSeconds, 0.375, 0.5);
            Assert.InRange(DecisionNodeClient.Backoff(10).TotalSeconds, 6.0, 8.0);
        }
    }

    [Fact]
    public void RetryWaitPrefersMilliseconds()
    {
        var headers = new Dictionary<string, string> { ["retry-after"] = "1", ["retry-after-ms"] = "200" };
        Assert.Equal(TimeSpan.FromMilliseconds(200), DecisionNodeClient.RetryWait(n => headers.GetValueOrDefault(n)));
        Assert.Null(DecisionNodeClient.RetryWait(_ => null));
    }

    [Fact]
    public async Task ServerWaitIsCappedAtSixtySeconds()
    {
        var stub = new StubHandler((_, i) => i == 0 ? Json(529, Overloaded, ("retry-after-ms", "600000")) : Ok());
        using var dn = Client(stub);
        var waits = new List<TimeSpan>();
        dn.Delay = (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        };
        await dn.DecideAsync("s", OneTruth());
        Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, waits);
    }
}

public class DecideManyTests
{
    private static DecideRequest Item(string state) => new() { State = state, Questions = OneTruth() };

    [Fact]
    public async Task ResultsComeBackInInputOrderWithFailuresInPlace()
    {
        var stub = new StubHandler(async (r, _, ct) =>
        {
            var state = (string)r.Json!["state"]!;
            await Task.Delay(state == "a" ? 150 : 10, ct);
            return state == "b"
                ? Json(422, """{"detail":[{"type":"missing"}]}""")
                : Ok("""{"answers":{"q":{"type":"truth","truth":TRUTH}}}""".Replace("TRUTH", state == "a" ? "0.1" : "0.3"));
        });
        using var dn = Client(stub);
        var results = await dn.DecideManyAsync(new[] { Item("a"), Item("b"), Item("c") });
        Assert.Equal(3, results.Count);
        Assert.Equal(0.1, results[0].GetDecisionOrThrow().Truth("q").Truth);
        Assert.False(results[1].IsSuccess);
        Assert.IsType<ValidationError>(results[1].Error);
        Assert.Throws<ValidationError>(() => results[1].GetDecisionOrThrow());
        Assert.Equal(0.3, results[2].Decision!.Truth("q").Truth);
    }

    [Fact]
    public async Task ConcurrencyIsLimited()
    {
        var stub = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(40, ct);
            return Ok();
        });
        using var dn = Client(stub);
        var results = await dn.DecideManyAsync(Enumerable.Range(0, 9).Select(i => Item("s" + i)), concurrency: 3);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.InRange(stub.MaxInFlight, 1, 3);
    }

    [Fact]
    public async Task DefaultConcurrencyIsEight()
    {
        var stub = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(100, ct);
            return Ok();
        });
        using var dn = Client(stub);
        await dn.DecideManyAsync(Enumerable.Range(0, 16).Select(i => Item("s" + i)));
        Assert.InRange(stub.MaxInFlight, 2, 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ConcurrencyBelowOneIsAnArgumentError(int concurrency)
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dn.DecideManyAsync(new[] { Item("a") }, concurrency));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task AnArgumentErrorInAnyRequestFailsBeforeAnythingIsSent()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await Assert.ThrowsAsync<ArgumentException>(() => dn.DecideManyAsync(new[] { Item("y"), new DecideRequest { State = "x", Questions = new Dictionary<string, Question>() } }));
        Assert.Empty(stub.Requests);
    }
}
