using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Usage by key, and the errors of a key's kind and spend cap.</summary>
public class KeyUsageTests
{
    private const string KeyId = "key_00000000000000000000000000000d01";
    private const string Page = """{"period":{"start":"2026-09-01T00:00:00Z","end":"2026-10-01T00:00:00Z"},"as_of":"2026-10-14T00:00:00Z","items":[],"next_cursor":null}""";

    private static UsageQuery September() =>
        new() { Start = "2026-09-01T00:00:00Z", End = "2026-10-01T00:00:00Z" };

    [Fact]
    public async Task KeySendsOnlyWhatIsGivenWithTheIdEscaped()
    {
        var stub = new StubHandler((_, _) => Ok("""{"key":{"id":"key_1"},"totals":{}}"""));
        using var dn = Client(stub);
        await dn.Usage.KeyAsync(KeyId);
        await dn.Usage.KeyAsync("key_a/b", "2026-09-01T00:00:00Z", "2026-10-01T00:00:00Z", "day", "model");
        var sent = stub.Requests.ToList();
        Assert.Equal(($"/v1/keys/{KeyId}/usage", ""), (sent[0].Uri.AbsolutePath, sent[0].Uri.Query));
        Assert.Equal("/v1/keys/key_a%2Fb/usage", sent[1].Uri.AbsolutePath.Replace("%2f", "%2F"));
        Assert.Equal("?start=2026-09-01T00%3A00%3A00Z&end=2026-10-01T00%3A00%3A00Z&granularity=day&group_by=model", sent[1].Uri.Query);
    }

    [Fact]
    public async Task ListSendsLabelsSortedAndEachKeyIdItsOwn()
    {
        var stub = new StubHandler((_, _) => Ok(Page));
        using var dn = Client(stub);
        var page = await dn.Usage.ListAsync(new UsageQuery
        {
            Start = "2026-09-01T00:00:00Z",
            End = "2026-10-01T00:00:00Z",
            Labels = new Dictionary<string, string> { ["plan"] = "pro", ["customer"] = "acme" },
            DeviceId = "drone-1",
            KeyIds = new[] { KeyId, "key_00000000000000000000000000000d02" },
            Scope = "decide",
            Status = "active",
            IncludeEmpty = false,
            Limit = 50,
        }, cursor: "abc");
        Assert.Equal("2026-10-14T00:00:00Z", (string?)page["as_of"]);
        var query = Uri.UnescapeDataString(Assert.Single(stub.Requests).Uri.Query);
        Assert.Equal($"?start=2026-09-01T00:00:00Z&end=2026-10-01T00:00:00Z&label=customer:acme&label=plan:pro&device_id=drone-1&key_id={KeyId}&key_id=key_00000000000000000000000000000d02&scope=decide&status=active&include_empty=false&limit=50&cursor=abc", query);
    }

    [Fact]
    public async Task AllFollowsTheCursorUntilItIsNull()
    {
        var stub = new StubHandler((r, i) => i switch
        {
            0 => Ok("""{"items":[{"key":{"id":"a"}},{"key":{"id":"b"}}],"next_cursor":"c1"}"""),
            1 => Ok("""{"items":[{"key":{"id":"c"}}],"next_cursor":"c2"}"""),
            _ => Ok("""{"items":[],"next_cursor":null}"""),
        });
        using var dn = Client(stub);
        var items = await dn.Usage.AllAsync(September());
        Assert.Equal(new[] { "a", "b", "c" }, items.Select(i => (string?)i["key"]!["id"]));
        var queries = stub.Requests.Select(r => r.Uri.Query).ToList();
        Assert.Equal(3, queries.Count);
        Assert.DoesNotContain("cursor", queries[0]);
        Assert.EndsWith("&cursor=c1", queries[1]);
        Assert.EndsWith("&cursor=c2", queries[2]);
    }

    [Theory]
    [InlineData("granularity", "week", "granularity is none, hour, day or month.")]
    [InlineData("group_by", "key", "group_by is model or channel.")]
    [InlineData("scope", "publishable", "scope is full, decide or keys.")]
    [InlineData("status", "gone", "status is active, expired, revoked or all.")]
    [InlineData("device_id", " drone", "device_id is 1 to 128 printable ASCII characters, not starting with a space.")]
    [InlineData("key_id", "key_ABC", "key_id is a key's id, key_ and 32 hex digits.")]
    [InlineData("label", "Customer", "A label filter is name:value, as labels are written.")]
    [InlineData("limit", "0", "limit is a whole number from 1 to 1000.")]
    public async Task AFilterTheApiRefusesIsRefusedLocally(string field, string value, string message)
    {
        var stub = new StubHandler((_, _) => Ok(Page));
        using var dn = Client(stub);
        var query = field switch
        {
            "granularity" => new UsageQuery { Start = "s", End = "e", Granularity = value },
            "group_by" => new UsageQuery { Start = "s", End = "e", GroupBy = value },
            "scope" => new UsageQuery { Start = "s", End = "e", Scope = value },
            "status" => new UsageQuery { Start = "s", End = "e", Status = value },
            "device_id" => new UsageQuery { Start = "s", End = "e", DeviceId = value },
            "key_id" => new UsageQuery { Start = "s", End = "e", KeyIds = new[] { value } },
            "label" => new UsageQuery { Start = "s", End = "e", Labels = new Dictionary<string, string> { [value] = "acme" } },
            _ => new UsageQuery { Start = "s", End = "e", Limit = int.Parse(value) },
        };
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.Usage.ListAsync(query));
        Assert.Equal(("api_usage_error", message), (error.ErrorType, error.Message));
        Assert.Null(error.Status);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task AHundredAndFirstKeyIdIsRefused()
    {
        var stub = new StubHandler((_, _) => Ok(Page));
        using var dn = Client(stub);
        var ids = Enumerable.Range(0, 101).Select(i => "key_" + i.ToString("x32")).ToList();
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.Usage.ListAsync(new UsageQuery { Start = "s", End = "e", KeyIds = ids }));
        Assert.Equal("At most 100 key_id filters.", error.Message);
        await dn.Usage.ListAsync(new UsageQuery { Start = "s", End = "e", KeyIds = ids.Take(100).ToList() });
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ASpendCapIsRaisedAtOnceAndOther429sRetry()
    {
        var capped = new StubHandler((_, _) => Json(429, """{"detail":{"error_type":"key_spend_cap_reached","message":"This key reached its daily spend cap."}}""", ("retry-after", "3600")));
        using (var dn = Client(capped, maxRetries: 2))
        {
            var error = await Assert.ThrowsAsync<RateLimitError>(() => dn.DecideAsync("s", OneTruth()));
            Assert.Equal("key_spend_cap_reached", error.ErrorType);
            Assert.Equal(TimeSpan.FromSeconds(60), error.RetryAfter);
        }
        Assert.Single(capped.Requests);

        var busy = new StubHandler((_, i) => i == 0
            ? Json(429, """{"detail":{"error_type":"rate_limit_error","message":"","details":{"reason":"client_rate"}}}""", ("retry-after-ms", "1"))
            : Ok());
        using (var dn = Client(busy, maxRetries: 2))
        {
            await dn.DecideAsync("s", OneTruth());
        }
        Assert.Equal(2, busy.Requests.Count);
    }

    [Fact]
    public async Task APublishableKeysRefusalsArePermissionDeniedWithTheirDetails()
    {
        var stub = new StubHandler((_, _) => Json(403, """{"detail":{"error_type":"model_not_allowed","message":"This key may not ask this model. Its models: decisionnode-flash.","details":{"allowed":["decisionnode-flash"]}}}"""));
        using var dn = Client(stub, maxRetries: 2);
        var error = await Assert.ThrowsAsync<PermissionDeniedError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Equal("model_not_allowed", error.ErrorType);
        Assert.Equal("""{"allowed":["decisionnode-flash"]}""", error.Details!.ToJsonString());
        Assert.Single(stub.Requests);
    }
}
