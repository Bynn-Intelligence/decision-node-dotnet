using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

public class BatchTests
{
    private const string Draft = """{"id":"batch_01J9ZB8Q3T","state":"draft","counters":{"requests":0,"queued":0,"in_progress":0,"completed":0,"failed":0,"expired":0,"cancelled":0},"created_at":"2026-10-06T09:12:04Z","finalized_at":null,"expires_at":null,"ended_at":null,"results_url":null,"results_expire_at":null,"price_factor":0.5,"usage":{"input_tokens":0,"output_tokens":0},"error":null}""";

    private static string WithState(string state, bool ended = false)
    {
        var o = JsonNode.Parse(Draft)!.AsObject();
        o["state"] = state;
        if (ended)
        {
            o["ended_at"] = "2026-10-06T10:00:00Z";
        }
        return o.ToJsonString();
    }

    private static BatchRequest Line(string id, object? state = null) => new() { CustomId = id, State = state, Questions = OneTruth("Refund?") };

    [Fact]
    public async Task CreateSendsJsonLinesWithModelAndState()
    {
        var stub = new StubHandler((_, _) => Json(201, Draft));
        using var dn = Client(stub);
        var batch = await dn.Batches.CreateAsync(new[] { Line("a", "charged twice"), new BatchRequest { CustomId = "b", Questions = OneTruth(), Model = "decisionnode-flash-latest" } });
        var sent = Assert.Single(stub.Requests);
        Assert.Equal("application/x-ndjson", sent.Headers["Content-Type"]);
        var lines = sent.Body!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("""{"custom_id":"a","model":"decisionnode-latest","state":"charged twice","questions":{"q":{"type":"truth","instructions":"Refund?"}}}""", lines[0]);
        Assert.Equal("""{"custom_id":"b","model":"decisionnode-flash-latest","state":"","questions":{"q":{"type":"truth","instructions":"?"}}}""", lines[1]);
        Assert.Equal("batch_01J9ZB8Q3T", batch.Id);
        Assert.Equal(0.5, batch.PriceFactor);
    }

    [Fact]
    public async Task AnAddOverTenMebibytesIsSplitInOrder()
    {
        var stub = new StubHandler((_, _) => Ok(Draft));
        using var dn = Client(stub);
        var big = new string('x', 4 * 1024 * 1024);
        await dn.Batches.AddAsync("batch_01J9ZB8Q3T", new[] { Line("0", big), Line("1", big), Line("2", big) });
        var calls = stub.Requests.Select(r => r.Body!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).ToList();
        Assert.Equal(new[] { 2, 1 }, calls);
        Assert.All(stub.Requests, r => Assert.True(r.Body!.Length <= BatchesClient.MaxAddBytes));
        await Assert.ThrowsAsync<ArgumentException>(() => dn.Batches.AddAsync("batch_01J9ZB8Q3T", Array.Empty<BatchRequest>()));
    }

    [Fact]
    public async Task CreateIsNotRetriedAfterAServerErrorButIsAfter529()
    {
        var stub = new StubHandler((_, i) => i == 0 ? Json(500, """{"detail":{"error_type":"api_error","message":"x"}}""") : Json(201, Draft));
        using var dn = Client(stub);
        dn.Delay = (_, _) => Task.CompletedTask;
        await Assert.ThrowsAsync<ServerError>(() => dn.Batches.CreateAsync(new[] { Line("a") }));
        Assert.Single(stub.Requests);

        var overloaded = new StubHandler((_, i) => i == 0 ? Json(529, """{"detail":{"error_type":"overloaded_error","message":"x"}}""") : Json(201, Draft));
        using var dn2 = Client(overloaded);
        dn2.Delay = (_, _) => Task.CompletedTask;
        await dn2.Batches.CreateAsync(new[] { Line("a") });
        Assert.Equal(2, overloaded.Requests.Count);
    }

    [Fact]
    public async Task AFinalizeAnswered409IsReadWithGet()
    {
        var stub = new StubHandler((r, _) => r.Method == HttpMethod.Post
            ? Json(409, """{"detail":{"error_type":"invalid_batch_state","message":"queued"}}""")
            : Ok(WithState("queued")));
        using var dn = Client(stub);
        var batch = await dn.Batches.FinalizeAsync("batch_01J9ZB8Q3T");
        Assert.Equal("queued", batch.State);

        var draft = new StubHandler((r, _) => r.Method == HttpMethod.Post
            ? Json(409, """{"detail":{"error_type":"invalid_batch_state","message":"x"}}""")
            : Ok(Draft));
        using var dn2 = Client(draft);
        var error = await Assert.ThrowsAsync<DecisionNodeError>(() => dn2.Batches.FinalizeAsync("batch_01J9ZB8Q3T"));
        Assert.Equal(409, error.Status);
    }

    [Fact]
    public async Task ResultsAreReadLineByLineWithDecisionsAndErrorValues()
    {
        var file = string.Join("\n",
            """{"custom_id":"a","status":200,"body":{"answers":{"q":{"type":"truth","truth":0.8200000000000001}}},"headers":{"x-request-id":"req_1"}}""",
            """{"custom_id":"b","status":402,"body":{"detail":{"error_type":"insufficient_credit","message":"empty"}},"headers":{"x-decisionnode-error":"balance"}}""",
            """{"custom_id":"c","status":"expired","body":null,"headers":{}}""") + "\n";
        var stub = new StubHandler((_, _) => Json(200, file));
        using var dn = Client(stub);
        var results = new List<BatchResult>();
        await foreach (var r in dn.Batches.ResultsAsync("batch_01J9ZB8Q3T"))
        {
            results.Add(r);
        }
        Assert.Equal(0.8200000000000001, results[0].Decision!.Truth("q").Truth);
        Assert.Equal("req_1", results[0].Decision!.RequestId);
        Assert.IsType<InsufficientCreditError>(results[1].Error);
        Assert.Equal("balance", results[1].Error!.Reason);
        Assert.Null(results[2].Status);
        Assert.Equal("expired", results[2].Outcome);
        Assert.Null(results[2].Decision);
        Assert.Null(results[2].Error);
    }

    [Fact]
    public async Task RunBatchRefusesAPollIntervalBelowTenSecondsBeforeSending()
    {
        var stub = new StubHandler((_, _) => Ok(Draft));
        using var dn = Client(stub);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dn.RunBatchAsync(new[] { Line("a") }, TimeSpan.FromSeconds(9)));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task RunBatchPollsAtTheIntervalOrTheServersLongerWait()
    {
        var gets = 0;
        var stub = new StubHandler((r, _) =>
        {
            var path = r.Uri.AbsolutePath;
            if (r.Method == HttpMethod.Get && path.EndsWith("/results", StringComparison.Ordinal))
            {
                return Json(200, """{"custom_id":"a","status":200,"body":{"answers":{}},"headers":{}}""" + "\n");
            }
            if (r.Method == HttpMethod.Get)
            {
                return ++gets < 3 ? Ok(WithState("running"), ("retry-after", "30")) : Ok(WithState("completed", ended: true));
            }
            if (path.EndsWith("/finalize", StringComparison.Ordinal))
            {
                return Ok(WithState("queued"));
            }
            return Json(path.EndsWith("/requests", StringComparison.Ordinal) ? 200 : 201, Draft);
        });
        using var dn = Client(stub);
        var waits = new List<TimeSpan>();
        dn.Delay = (w, _) =>
        {
            waits.Add(w);
            return Task.CompletedTask;
        };
        var run = await dn.RunBatchAsync(new[] { Line("a") }, TimeSpan.FromSeconds(10));
        Assert.Equal("completed", run.Batch.State);
        Assert.Equal(new[] { 10.0, 30.0, 30.0 }, waits.Select(w => w.TotalSeconds));
        var count = 0;
        await foreach (var _ in run.Results)
        {
            count++;
        }
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RunBatchCancelsTheDraftWhenAnAddFails()
    {
        var stub = new StubHandler((r, _) => r.Uri.AbsolutePath.EndsWith("/requests", StringComparison.Ordinal)
            ? Json(400, """{"detail":{"error_type":"api_usage_error","message":"bad"}}""")
            : Json(r.Uri.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal) ? 200 : 201, Draft));
        using var dn = Client(stub);
        await Assert.ThrowsAsync<BadRequestError>(() => dn.RunBatchAsync(new[] { Line("a") }, TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "/v1/batches", "/v1/batches/batch_01J9ZB8Q3T/requests", "/v1/batches/batch_01J9ZB8Q3T/cancel" }, stub.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task RunBatchRaisesTimeoutExceptionPastItsTimeout()
    {
        var stub = new StubHandler((r, _) => r.Method == HttpMethod.Get
            ? Ok(WithState("running"))
            : Json(r.Uri.AbsolutePath.EndsWith("/finalize", StringComparison.Ordinal) ? 200 : 201, r.Uri.AbsolutePath.EndsWith("/finalize", StringComparison.Ordinal) ? WithState("queued") : Draft));
        using var dn = Client(stub);
        dn.Delay = (_, _) => Task.CompletedTask;
        await Assert.ThrowsAsync<TimeoutException>(() => dn.RunBatchAsync(new[] { Line("a") }, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(35)));
    }
}
