using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Items found by reference, edited and described: metadata, UpdateItemAsync, the filters and the errors' details.</summary>
public class CollectionItemsTests
{
    private const string Id = "col_3f6c1a9e0b2d4c7f8a1e5d9b2c4f6a80";
    private const string ItemId = "itm_9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d";
    private const string Item = """{"id":"itm_9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d","reference":"emp-1042"}""";
    private static readonly Image Badge = Image.FromUrl("badge", "https://files.example.com/badges/emp-1042.jpg");

    private static void Same(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual?.ToJsonString());

    private static async Task<JsonArray> Refused(Func<DecisionNodeClient, Task> call)
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<ValidationError>(() => call(dn));
        Assert.Null(error.Status);
        Assert.Empty(stub.Requests);
        return error.Entries!;
    }

    [Fact]
    public async Task EnrollSendsMetadataOnlyWhenGiven()
    {
        var stub = new StubHandler((_, _) => Json(201, Item));
        using var dn = Client(stub);
        await dn.Collections.EnrollAsync(Id, Badge, "emp-1042", metadata: new JsonObject { ["department"] = "reception", ["floor"] = 3, ["badge_active"] = true });
        await dn.Collections.EnrollAsync(Id, Badge, "emp-1042");
        var sent = stub.Requests.ToList();
        Same("""{"department":"reception","floor":3,"badge_active":true}""", sent[0].Json!["metadata"]);
        Assert.False(sent[1].Json!.AsObject().ContainsKey("metadata"));
    }

    [Fact]
    public async Task MetadataIsCheckedLocallyInKeyOrder()
    {
        var tooMany = new JsonObject();
        for (var i = 0; i < 21; i++)
        {
            tooMany["k" + i] = "Bad Key";
        }
        Same("""[{"type":"value_error","loc":["body","metadata"],"msg":"Metadata holds at most 20 keys.","input":null}]""",
            await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, "emp-1042", metadata: tooMany)));

        var metadata = new JsonObject
        {
            ["Department"] = "x",
            ["note"] = new string('t', 501),
            ["tags"] = new JsonArray("a"),
            ["gone"] = null,
            ["bell\u0007" + new string('k', 45)] = 1,
            ["ok"] = 2.5,
        };
        var entries = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, "emp-1042", metadata: metadata));
        Same("""
            [{"type":"value_error","loc":["body","metadata"],"msg":"A metadata key is 1 to 40 characters of a-z, 0-9 and _: \"Department\".","input":null},
             {"type":"value_error","loc":["body","metadata","note"],"msg":"A metadata text is at most 500 characters.","input":"tttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttttt..."},
             {"type":"value_error","loc":["body","metadata","tags"],"msg":"A metadata value is a text, a number or true or false.","input":null},
             {"type":"value_error","loc":["body","metadata","gone"],"msg":"A metadata value is a text, a number or true or false.","input":null},
             {"type":"value_error","loc":["body","metadata"],"msg":"A metadata key is 1 to 40 characters of a-z, 0-9 and _: \"bell?kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk...\".","input":null}]
            """, entries);
    }

    [Fact]
    public async Task UpdateItemSendsOnlyTheFieldsGivenWithAKey()
    {
        var stub = new StubHandler((_, _) => Ok(Item));
        using var dn = Client(stub);
        var item = await dn.Collections.UpdateItemAsync(Id, ItemId, new CollectionItemUpdate { ClearLabel = true });
        Assert.Equal(Item, item.ToJsonString());
        await dn.Collections.UpdateItemAsync(Id, ItemId, new CollectionItemUpdate { Metadata = new JsonObject() });
        await dn.Collections.UpdateItemAsync(Id, ItemId, new CollectionItemUpdate { Reference = "emp-1043", Label = "Front desk" });
        var sent = stub.Requests.ToList();
        Assert.All(sent, r => Assert.Equal((HttpMethod.Patch, $"/v1/collections/{Id}/items/{ItemId}"), (r.Method, r.Uri.AbsolutePath)));
        Assert.Equal(new[] { """{"label":null}""", """{"metadata":{}}""", """{"reference":"emp-1043","label":"Front desk"}""" }, sent.Select(r => r.Body));
        Assert.All(sent, r => Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", r.Headers["Idempotency-Key"]));
        Assert.Equal(3, sent.Select(r => r.Headers["Idempotency-Key"]).Distinct().Count());
    }

    [Fact]
    public async Task UpdateItemRetriesWithItsKey()
    {
        var stub = new StubHandler((_, i) => i == 0 ? Json(500, """{"detail":{"error_type":"api_error","message":""}}""") : Ok(Item));
        using var dn = Client(stub, maxRetries: 2);
        await dn.Collections.UpdateItemAsync(Id, ItemId, new CollectionItemUpdate { Label = "Front desk" });
        Assert.Equal(2, stub.Requests.Count);
        Assert.Single(stub.Requests.Select(r => r.Headers["Idempotency-Key"]).Distinct());
    }

    [Fact]
    public async Task UpdateItemChecksItsFieldsLocally()
    {
        Same("""[{"type":"missing","loc":["body"],"msg":"Send reference, label, metadata or any of them","input":null}]""",
            await Refused(dn => dn.Collections.UpdateItemAsync(Id, ItemId, new CollectionItemUpdate())));
        var reference = await Refused(dn => dn.Collections.UpdateItemAsync(Id, ItemId, new CollectionItemUpdate { Reference = "emp 1042" }));
        Assert.Equal(("string_pattern_mismatch", """["body","reference"]"""), ((string?)reference[0]!["type"], reference[0]!["loc"]!.ToJsonString()));
        var label = await Refused(dn => dn.Collections.UpdateItemAsync(Id, ItemId, new CollectionItemUpdate { Label = new string('l', 129) }));
        Assert.Equal("string_too_long", (string?)label[0]!["type"]);
        using var dn2 = Client(new StubHandler((_, _) => Ok(Item)));
        await Assert.ThrowsAsync<ArgumentException>(() => dn2.Collections.UpdateItemAsync(Id, ItemId, new CollectionItemUpdate { Label = "x", ClearLabel = true }));
    }

    [Fact]
    public async Task ListItemsFiltersByReferenceOrPrefix()
    {
        var stub = new StubHandler((_, _) => Ok("""{"items":[],"offset":0,"limit":20,"total":0,"total_capped":false,"next_offset":null,"reference":"emp:1042","reference_prefix":null}"""));
        using var dn = Client(stub);
        var page = await dn.Collections.ListItemsAsync(Id, reference: "emp:1042");
        Assert.False((bool)page["total_capped"]!);
        await dn.Collections.ListItemsAsync(Id, limit: 5, referencePrefix: "emp-");
        Assert.Equal(new[] { "?reference=emp%3A1042", "?limit=5&reference_prefix=emp-" }, stub.Requests.Select(r => r.Uri.Query));

        var both = await Assert.ThrowsAsync<BadRequestError>(() => dn.Collections.ListItemsAsync(Id, reference: "a", referencePrefix: "b"));
        Assert.Equal(("api_usage_error", "Send query parameter reference or reference_prefix, not both."), (both.ErrorType, both.Message));
        var empty = await Assert.ThrowsAsync<BadRequestError>(() => dn.Collections.ListItemsAsync(Id, referencePrefix: ""));
        Assert.Equal("Query parameter reference_prefix must be 1 to 64 characters of A-Z, a-z, 0-9, _, ., : and -, got .", empty.Message);
        var odd = await Assert.ThrowsAsync<BadRequestError>(() => dn.Collections.ListItemsAsync(Id, reference: "emp 1é\n" + new string('x', 70)));
        Assert.Equal($"Query parameter reference must be 1 to 64 characters of A-Z, a-z, 0-9, _, ., : and -, got emp 1\\u{{e9}}\\u{{a}}{new string('x', 57)}....", odd.Message);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task AnUnpricedEnrollmentIsNeverRetriedAndCarriesItsDetails()
    {
        var stub = new StubHandler((_, _) => Json(529, """{"detail":{"error_type":"overloaded_error","message":"Face enrollment is not available on this deployment yet.","details":{"reason":"enrollment_unpriced"}}}"""));
        using var dn = Client(stub, maxRetries: 2);
        var error = await Assert.ThrowsAsync<OverloadedError>(() => dn.Collections.EnrollAsync(Id, Badge, "emp-1042"));
        Same("""{"reason":"enrollment_unpriced"}""", error.Details);
        Assert.Single(stub.Requests);

        var busy = new StubHandler((_, i) => i == 0
            ? Json(529, """{"detail":{"error_type":"overloaded_error","message":"","details":{"reason":"face_model_unavailable"}}}""", ("retry-after-ms", "1"))
            : Json(201, Item));
        using var dn2 = Client(busy, maxRetries: 2);
        await dn2.Collections.EnrollAsync(Id, Badge, "emp-1042");
        Assert.Equal(2, busy.Requests.Count);
    }

    [Fact]
    public async Task DetailsAreEmptyWhenNotAnObjectAndKeyScopeIsPermissionDenied()
    {
        var stub = new StubHandler((_, _) => Json(403, """{"detail":{"error_type":"key_scope_denied","message":"This key may not decide.","details":"x"}}"""));
        using var dn = Client(stub, maxRetries: 2);
        var error = await Assert.ThrowsAsync<PermissionDeniedError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Null(error.Details);
        Assert.Equal("key_scope_denied", error.ErrorType);
        Assert.Single(stub.Requests);
    }
}
