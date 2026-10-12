using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Collections of faces: the seven calls, their local checks and their retries.</summary>
public class CollectionsTests
{
    private const string Id = "col_3f6c1a9e0b2d4c7f8a1e5d9b2c4f6a80";
    private const string Created = """{"id":"col_3f6c1a9e0b2d4c7f8a1e5d9b2c4f6a80","type":"face","name":"Staff","public":false,"items":0,"created_at":"2026-10-12T09:00:00Z","deletion":null}""";
    private static readonly Image Badge = Image.FromUrl("badge", "https://files.example.com/badges/emp-1042.jpg");

    [Fact]
    public async Task CreateSendsTheTypeAndNameAndReturnsTheObjectAsSent()
    {
        var stub = new StubHandler((_, _) => Json(201, Created));
        using var dn = Client(stub);
        var collection = await dn.Collections.CreateAsync("Staff");
        var r = Assert.Single(stub.Requests);
        Assert.Equal((HttpMethod.Post, "/v1/collections"), (r.Method, r.Uri.AbsolutePath));
        Assert.Equal("""{"type":"face","name":"Staff"}""", r.Body);
        Assert.Equal(Created, collection.ToJsonString());
    }

    [Fact]
    public async Task TheReadsAndDeletesNameTheIdInThePath()
    {
        var stub = new StubHandler((r, _) => r.Uri.AbsolutePath == "/v1/collections"
            ? Ok("""{"collections":[{"id":"col_b"},{"id":"celebrities","public":true}]}""")
            : Ok("""{"id":"x","deleted":true}"""));
        using var dn = Client(stub);
        var list = await dn.Collections.ListAsync();
        Assert.Equal(new[] { "col_b", "celebrities" }, list.Select(c => (string?)c["id"]));
        await dn.Collections.GetAsync("celebrities");
        await dn.Collections.DeleteAsync(Id);
        await dn.Collections.DeleteItemAsync(Id, "itm_1");
        var sent = stub.Requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}").ToList();
        Assert.Equal(new[]
        {
            "GET /v1/collections",
            "GET /v1/collections/celebrities",
            $"DELETE /v1/collections/{Id}",
            $"DELETE /v1/collections/{Id}/items/itm_1",
        }, sent);
    }

    [Fact]
    public async Task EnrollSendsTheImageAsADecisionDoesAndTheLabelOnlyWhenGiven()
    {
        var stub = new StubHandler((_, _) => Json(201, """{"id":"itm_1","reference":"emp-1042"}"""));
        using var dn = Client(stub);
        await dn.Collections.EnrollAsync(Id, Badge, "emp-1042", "Reception");
        await dn.Collections.EnrollAsync(Id, Badge, "emp-1042");
        var sent = stub.Requests.ToList();
        Assert.Equal($"/v1/collections/{Id}/items", sent[0].Uri.AbsolutePath);
        Assert.Equal("""{"image":{"id":"badge","url":"https://files.example.com/badges/emp-1042.jpg"},"reference":"emp-1042","label":"Reception"}""", sent[0].Body);
        Assert.Equal("""{"image":{"id":"badge","url":"https://files.example.com/badges/emp-1042.jpg"},"reference":"emp-1042"}""", sent[1].Body);
    }

    [Fact]
    public async Task ListItemsSendsOffsetAndLimitOnlyWhenGiven()
    {
        var stub = new StubHandler((_, _) => Ok("""{"items":[],"offset":0,"limit":20,"total":0,"next_offset":null}"""));
        using var dn = Client(stub);
        await dn.Collections.ListItemsAsync(Id);
        await dn.Collections.ListItemsAsync(Id, offset: 20, limit: 5);
        Assert.Equal(new[] { "", "?offset=20&limit=5" }, stub.Requests.Select(r => r.Uri.Query));
        var offset = await Assert.ThrowsAsync<BadRequestError>(() => dn.Collections.ListItemsAsync(Id, offset: -1));
        Assert.Equal("Query parameter offset must be an integer of 0 or more, got -1.", offset.Message);
        var limit = await Assert.ThrowsAsync<BadRequestError>(() => dn.Collections.ListItemsAsync(Id, limit: 101));
        Assert.Equal(("api_usage_error", "Query parameter limit must be an integer from 1 to 100, got 101."), (limit.ErrorType, limit.Message));
        Assert.Equal(2, stub.Requests.Count);
    }

    private static void Same(string expected, JsonNode actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual.ToJsonString());

    private static async Task<JsonObject> Refused(Func<DecisionNodeClient, Task> call)
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<ValidationError>(() => call(dn));
        Assert.Null(error.Status);
        Assert.Null(error.ErrorType);
        Assert.Empty(stub.Requests);
        return (JsonObject)Assert.Single(error.Entries!)!;
    }

    [Fact]
    public async Task CreateChecksTheTypeAndTheNameLocally()
    {
        var type = await Refused(dn => dn.Collections.CreateAsync("Staff", "person"));
        Same("""{"type":"literal_error","loc":["body","type"],"msg":"Input should be 'face'","input":"person","ctx":{"expected":"'face'"}}""", type);
        var empty = await Refused(dn => dn.Collections.CreateAsync(""));
        Same("""{"type":"string_too_short","loc":["body","name"],"msg":"String should have at least 1 character","input":"","ctx":{"min_length":1}}""", empty);
        var name = new string('n', 81);
        var tooLong = await Refused(dn => dn.Collections.CreateAsync(name));
        Assert.Equal(("string_too_long", 80), ((string?)tooLong["type"], (int)tooLong["ctx"]!["max_length"]!));
    }

    [Fact]
    public async Task NamesReferencesAndLabelsAreCountedInCodePoints()
    {
        // 80 characters outside the Basic Multilingual Plane are 160 UTF-16 units: sent.
        var stub = new StubHandler((_, _) => Json(201, Created));
        using var dn = Client(stub);
        await dn.Collections.CreateAsync(string.Concat(Enumerable.Repeat("\U00020000", 80)));
        await dn.Collections.EnrollAsync(Id, Badge, "emp-1042", string.Concat(Enumerable.Repeat("\U00020000", 128)));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task EnrollChecksTheReferenceTheLabelAndTheImageLocally()
    {
        var empty = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, ""));
        Assert.Equal("string_too_short", (string?)empty["type"]);
        Assert.Equal("""["body","reference"]""", empty["loc"]!.ToJsonString());
        var tooLong = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, new string('r', 65)));
        Assert.Equal(("string_too_long", 64), ((string?)tooLong["type"], (int)tooLong["ctx"]!["max_length"]!));
        var pattern = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, "emp 1042"));
        Same("""{"type":"string_pattern_mismatch","loc":["body","reference"],"msg":"String should match pattern '^[A-Za-z0-9_.:-]+$'","input":"emp 1042","ctx":{"pattern":"^[A-Za-z0-9_.:-]+$"}}""", pattern);
        var newline = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, "emp-1042\n"));
        Assert.Equal("string_pattern_mismatch", (string?)newline["type"]);
        var label = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, "emp-1042", new string('l', 129)));
        Assert.Equal(("string_too_long", """["body","label"]"""), ((string?)label["type"], label["loc"]!.ToJsonString()));

        // The image is checked as a decision's: a URL that is not https is the API's 400.
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await Assert.ThrowsAsync<BadRequestError>(() => dn.Collections.EnrollAsync(Id, Image.FromUrl("badge", "http://files.example.com/b.jpg"), "emp-1042"));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task CreateAndEnrollAreRetriedOnlyOn429And529()
    {
        var fails = new StubHandler((_, _) => Json(500, """{"detail":{"error_type":"api_error","message":""}}"""));
        using (var dn = Client(fails, maxRetries: 2))
        {
            await Assert.ThrowsAsync<ServerError>(() => dn.Collections.CreateAsync("Staff"));
            await Assert.ThrowsAsync<ServerError>(() => dn.Collections.EnrollAsync(Id, Badge, "emp-1042"));
        }
        Assert.Equal(2, fails.Requests.Count);

        var busy = new StubHandler((_, i) => i == 0 ? Json(529, """{"detail":{"error_type":"overloaded_error","message":""}}""", ("retry-after-ms", "1")) : Json(201, Created));
        using (var dn = Client(busy, maxRetries: 2))
        {
            await dn.Collections.CreateAsync("Staff");
        }
        Assert.Equal(2, busy.Requests.Count);

        var read = new StubHandler((_, i) => i == 0 ? Json(500, """{"detail":{"error_type":"api_error","message":""}}""") : Ok(Created));
        using (var dn = Client(read, maxRetries: 2))
        {
            await dn.Collections.GetAsync(Id);
        }
        Assert.Equal(2, read.Requests.Count);
    }

    [Fact]
    public async Task A409IsTheBaseError()
    {
        using var dn = Client(new StubHandler((_, _) => Json(409, """{"detail":{"error_type":"name_taken","message":"A collection named 'Staff' exists."}}""")));
        var error = await Assert.ThrowsAsync<DecisionNodeError>(() => dn.Collections.CreateAsync("Staff"));
        Assert.Equal(typeof(DecisionNodeError), error.GetType());
        Assert.Equal((409, "name_taken"), (error.Status, error.ErrorType));
    }
}
