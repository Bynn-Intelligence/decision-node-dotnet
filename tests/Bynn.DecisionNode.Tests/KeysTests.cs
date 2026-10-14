using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Device keys: make, list, change and revoke, the Idempotency-Key and the local checks.</summary>
public class KeysTests
{
    private const string KeyId = "key_7c2e9a41b0d34f6e8a5c1d2b3e4f5a60";
    private const string Key = """{"id":"key_7c2e9a41b0d34f6e8a5c1d2b3e4f5a60","name":"Kiosk 0412","scope":"decide","status":"active"}""";
    private const string Made = """{"key":""" + Key + ""","secret":"dn_live_0000"}""";
    private const string Uuid4 = "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";

    private static void Same(string expected, string? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual is null ? null : JsonNode.Parse(actual)), actual);

    [Fact]
    public async Task CreateSendsOnlyTheFieldsGivenWithAKey()
    {
        var stub = new StubHandler((_, _) => Json(201, Made));
        using var dn = Client(stub);
        var made = await dn.Keys.CreateAsync(new KeyCreate
        {
            Name = "Kiosk 0412",
            DeviceId = "kiosk-0412",
            Labels = new Dictionary<string, string> { ["store"] = "118" },
            Limits = new KeyLimits(RequestsPerSecond: 5),
            DailySpendCapUsd = 2.5m,
            ExpiresAt = "2027-10-13T00:00:00Z",
            MaySearchCollections = false,
        });
        await dn.Keys.CreateAsync(new KeyCreate { Name = "Door 7" }, idempotencyKey: "door-7");
        Assert.Equal("dn_live_0000", (string?)made["secret"]);
        var sent = stub.Requests.ToList();
        Assert.Equal((HttpMethod.Post, "/v1/keys"), (sent[0].Method, sent[0].Uri.AbsolutePath));
        Same("""{"name":"Kiosk 0412","device_id":"kiosk-0412","labels":{"store":"118"},"limits":{"requests_per_second":5},"daily_spend_cap":{"usd":2.5},"expires_at":"2027-10-13T00:00:00Z","may_search_collections":false}""", sent[0].Body);
        Assert.Matches(Uuid4, sent[0].Headers["Idempotency-Key"]);
        Assert.Equal("""{"name":"Door 7"}""", sent[1].Body);
        Assert.Equal("door-7", sent[1].Headers["Idempotency-Key"]);
    }

    [Fact]
    public async Task CreateKeepsItsKeyAcrossAttemptsAndWaitsOutAnotherRun()
    {
        var stub = new StubHandler((_, i) => i switch
        {
            0 => Json(500, """{"detail":{"error_type":"api_error","message":""}}"""),
            1 => Json(409, """{"detail":{"error_type":"idempotency_in_progress","message":"A request with this Idempotency-Key is still running. Try again shortly."}}""", ("retry-after-ms", "1")),
            _ => Json(201, Made),
        });
        using var dn = Client(stub, maxRetries: 2);
        await dn.Keys.CreateAsync(new KeyCreate { Name = "Kiosk 0412" });
        Assert.Equal(3, stub.Requests.Count);
        Assert.Single(stub.Requests.Select(r => r.Headers["Idempotency-Key"]).Distinct());
    }

    [Theory]
    [InlineData("")]
    [InlineData("café")]
    [InlineData("tab\there")]
    public async Task AnIdempotencyKeyTheApiRefusesIsRefusedLocally(string key)
    {
        var stub = new StubHandler((_, _) => Json(201, Made));
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.Keys.CreateAsync(new KeyCreate { Name = "x" }, key));
        Assert.Equal(("api_usage_error", "Idempotency-Key must be sent once, as 1 to 255 printable ASCII characters."), (error.ErrorType, error.Message));
        await Assert.ThrowsAsync<BadRequestError>(() => dn.Keys.CreateAsync(new KeyCreate { Name = "x" }, new string('k', 256)));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task ListSendsTheFiltersGivenAndAllFollowsTheCursor()
    {
        var stub = new StubHandler((r, i) => i switch
        {
            0 => Ok("""{"items":[],"next_cursor":null}"""),
            1 => Ok("""{"items":[{"id":"a"},{"id":"b"}],"next_cursor":"c1"}"""),
            _ => Ok("""{"items":[{"id":"c"}],"next_cursor":null}"""),
        });
        using var dn = Client(stub);
        await dn.Keys.ListAsync(new KeyListQuery
        {
            Labels = new Dictionary<string, string> { ["store"] = "118", ["fw"] = "2.4" },
            DeviceId = "kiosk-0412",
            Status = "all",
            Limit = 200,
        }, cursor: "x");
        var all = await dn.Keys.AllAsync();
        Assert.Equal(new[] { "a", "b", "c" }, all.Select(k => (string?)k["id"]));
        var queries = stub.Requests.Select(r => Uri.UnescapeDataString(r.Uri.Query)).ToList();
        Assert.Equal("?label=fw:2.4&label=store:118&device_id=kiosk-0412&status=all&limit=200&cursor=x", queries[0]);
        Assert.Equal(new[] { "", "?cursor=c1" }, queries.Skip(1));
    }

    [Theory]
    [InlineData("label", "A label filter is name:value, as labels are written.")]
    [InlineData("device_id", "device_id is 1 to 128 printable ASCII characters, not starting with a space.")]
    [InlineData("status", "status is active, expired, revoked or all.")]
    [InlineData("limit", "limit is a whole number from 1 to 200.")]
    public async Task AListFilterTheApiRefusesIsRefusedLocally(string field, string message)
    {
        var stub = new StubHandler((_, _) => Ok("""{"items":[],"next_cursor":null}"""));
        using var dn = Client(stub);
        var query = field switch
        {
            "label" => new KeyListQuery { Labels = new Dictionary<string, string> { ["Store"] = "118" } },
            "device_id" => new KeyListQuery { DeviceId = " kiosk" },
            "status" => new KeyListQuery { Status = "gone" },
            _ => new KeyListQuery { Limit = 201 },
        };
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.Keys.ListAsync(query));
        Assert.Equal(("api_usage_error", message), (error.ErrorType, error.Message));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task UpdateAndRevokeNameTheKey()
    {
        var stub = new StubHandler((r, _) => r.Method == HttpMethod.Delete ? Ok($$"""{"id":"{{KeyId}}","revoked":true}""") : Ok(Key));
        using var dn = Client(stub);
        await dn.Keys.UpdateAsync(KeyId, new KeyUpdate { Name = "Kiosk 12", ClearLabels = true, ClearDailySpendCap = true, ExpiresAt = "2027-01-01T00:00:00Z" });
        await dn.Keys.UpdateAsync(KeyId, new KeyUpdate { Limits = new KeyLimits(TokensPerSecond: 100), ClearExpiresAt = true, MaySearchCollections = true });
        var revoked = await dn.Keys.RevokeAsync(KeyId);
        Assert.True((bool)revoked["revoked"]!);
        var sent = stub.Requests.ToList();
        Assert.Equal(
            new[] { $"PATCH /v1/keys/{KeyId}", $"PATCH /v1/keys/{KeyId}", $"DELETE /v1/keys/{KeyId}" },
            sent.Select(r => $"{r.Method} {r.Uri.AbsolutePath}"));
        Same("""{"name":"Kiosk 12","labels":null,"daily_spend_cap":null,"expires_at":"2027-01-01T00:00:00Z"}""", sent[0].Body);
        Same("""{"limits":{"tokens_per_second":100},"expires_at":null,"may_search_collections":true}""", sent[1].Body);
        Assert.All(sent, r => Assert.False(r.Headers.ContainsKey("Idempotency-Key")));
    }

    [Fact]
    public async Task AFieldBothGivenAndClearedIsAnArgumentError()
    {
        var stub = new StubHandler((_, _) => Ok(Key));
        using var dn = Client(stub);
        await Assert.ThrowsAsync<ArgumentException>(() => dn.Keys.UpdateAsync(KeyId, new KeyUpdate { Labels = new Dictionary<string, string> { ["a"] = "b" }, ClearLabels = true }));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task RevokeByLabelsSendsTheSelectorAndDryRun()
    {
        var stub = new StubHandler((_, _) => Ok("""{"would_revoke":3,"more":false}"""));
        using var dn = Client(stub);
        var answer = await dn.Keys.RevokeByLabelsAsync(new Dictionary<string, string> { ["store"] = "118" }, new[] { "kiosk-1", "kiosk-2" }, dryRun: true);
        Assert.Equal(3, (int)answer["would_revoke"]!);
        await dn.Keys.RevokeByLabelsAsync(new Dictionary<string, string> { ["store"] = "118" });
        var sent = stub.Requests.ToList();
        Assert.All(sent, r => Assert.Equal((HttpMethod.Post, "/v1/keys/revoke"), (r.Method, r.Uri.AbsolutePath)));
        Same("""{"labels":{"store":"118"},"device_ids":["kiosk-1","kiosk-2"],"dry_run":true}""", sent[0].Body);
        Same("""{"labels":{"store":"118"}}""", sent[1].Body);
    }

    [Fact]
    public async Task AKeyOfAnotherKindIsPermissionDenied()
    {
        var stub = new StubHandler((_, _) => Json(403, """{"detail":{"error_type":"key_scope_denied","message":"Keys are managed with a keys key, made in the console."}}"""));
        using var dn = Client(stub, maxRetries: 2);
        var error = await Assert.ThrowsAsync<PermissionDeniedError>(() => dn.Keys.ListAsync());
        Assert.Equal("key_scope_denied", error.ErrorType);
        Assert.Single(stub.Requests);
    }

    private static async Task<string> Refused422(Func<DecisionNodeClient, Task> call)
    {
        var stub = new StubHandler((_, _) => Ok(Key));
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<ValidationError>(() => call(dn));
        Assert.Empty(stub.Requests);
        return Assert.Single(error.Entries!)!.ToJsonString();
    }

    [Fact]
    public async Task CreateChecksTheFixedRulesInTheBodysOrderTheFirstRefused()
    {
        Same("""{"type":"value_error","loc":["body","name"],"msg":"Give the key a name.","input":"   "}""",
            await Refused422(dn => dn.Keys.CreateAsync(new KeyCreate { Name = "   ", DeviceId = " bad" })));
        Same("""{"type":"value_error","loc":["body","name"],"msg":"Key names are at most 48 characters.","input":"nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn"}""",
            await Refused422(dn => dn.Keys.CreateAsync(new KeyCreate { Name = new string('n', 49) })));
        Same("""{"type":"value_error","loc":["body","device_id"],"msg":"device_id is 1 to 128 printable ASCII characters, not starting with a space.","input":" drone-9"}""",
            await Refused422(dn => dn.Keys.CreateAsync(new KeyCreate { Name = "Drone 9", DeviceId = " drone-9" })));
        var many = Enumerable.Range(0, 17).ToDictionary(i => $"l{i:00}", _ => "v");
        Same("""{"type":"value_error","loc":["body","labels"],"msg":"A key has at most 16 labels.","input":null}""",
            await Refused422(dn => dn.Keys.CreateAsync(new KeyCreate { Name = "Drone 9", Labels = many })));
        Same("""{"type":"value_error","loc":["body","labels"],"msg":"A label's name is 1 to 32 of a-z, 0-9, _ . and -.","input":null}""",
            await Refused422(dn => dn.Keys.CreateAsync(new KeyCreate { Name = "Drone 9", Labels = new Dictionary<string, string> { ["Site"] = "oslo" } })));
        Same("""{"type":"value_error","loc":["body","labels","site"],"msg":"A label's value is 1 to 64 characters of text.","input":null}""",
            await Refused422(dn => dn.Keys.CreateAsync(new KeyCreate { Name = "Drone 9", Labels = new Dictionary<string, string> { ["site"] = "" } })));
        foreach (var cap in new[] { 0.005m, 1000.01m, 0m })
        {
            Same("""{"type":"value_error","loc":["body","daily_spend_cap"],"msg":"daily_spend_cap.usd is from 0.01 to 1000, in cents, or null.","input":null}""",
                await Refused422(dn => dn.Keys.CreateAsync(new KeyCreate { Name = "Drone 9", DailySpendCapUsd = cap })));
        }
    }

    [Fact]
    public async Task UpdateWithNothingToChangeIsTheApis400()
    {
        var stub = new StubHandler((_, _) => Ok(Key));
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.Keys.UpdateAsync(KeyId, new KeyUpdate()));
        Assert.Equal("Nothing to change.", error.Message);
        Same("""{"type":"value_error","loc":["body","name"],"msg":"Give the key a name.","input":""}""",
            await Refused422(dn2 => dn2.Keys.UpdateAsync(KeyId, new KeyUpdate { Name = "" })));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task RevokeByLabelsNamesALabelAndAtMost1000Devices()
    {
        Same("""{"type":"value_error","loc":["body","labels"],"msg":"A bulk revoke names at least one label: every key it revokes has them all.","input":null}""",
            await Refused422(dn => dn.Keys.RevokeByLabelsAsync(new Dictionary<string, string>())));
        var devices = Enumerable.Range(0, 1001).Select(i => $"d-{i}").ToList();
        Same("""{"type":"value_error","loc":["body","device_ids"],"msg":"device_ids is a list of at most 1,000 device ids.","input":null}""",
            await Refused422(dn => dn.Keys.RevokeByLabelsAsync(new Dictionary<string, string> { ["customer"] = "acme" }, devices)));
        Same("""{"type":"value_error","loc":["body","device_id"],"msg":"device_id is 1 to 128 printable ASCII characters, not starting with a space.","input":" d"}""",
            await Refused422(dn => dn.Keys.RevokeByLabelsAsync(new Dictionary<string, string> { ["customer"] = "acme" }, new[] { "d-1", " d" })));
    }
}
