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
        Same("""{"type":"literal_error","loc":["body","type"],"msg":"Input should be 'face' or 'text'","input":"person","ctx":{"expected":"'face' or 'text'"}}""", type);
        var empty = await Refused(dn => dn.Collections.CreateAsync(""));
        Same("""{"type":"string_too_short","loc":["body","name"],"msg":"String should have at least 1 character","input":"","ctx":{"min_length":1}}""", empty);
        var name = new string('n', 81);
        var tooLong = await Refused(dn => dn.Collections.CreateAsync(name));
        Assert.Equal(("string_too_long", 80), ((string?)tooLong["type"], (int)tooLong["ctx"]!["max_length"]!));
        // The input is echoed within bounds: its first 64 characters and "...".
        Assert.Equal(new string('n', 64) + "...", (string?)tooLong["input"]);
        var longType = await Refused(dn => dn.Collections.CreateAsync("Staff", new string('t', 70)));
        Assert.Equal(new string('t', 64) + "...", (string?)longType["input"]);
    }

    [Fact]
    public async Task AnEchoedInputIsCutInCodePoints()
    {
        // 81 characters outside the Basic Multilingual Plane: 64 of them and "...", never a
        // surrogate cut in half.
        var wide = string.Concat(Enumerable.Repeat("\U00020000", 81));
        var entry = await Refused(dn => dn.Collections.CreateAsync(wide));
        Assert.Equal(string.Concat(Enumerable.Repeat("\U00020000", 64)) + "...", (string?)entry["input"]);
        // Exactly 64 characters are echoed whole.
        var exact = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, "emp 1042" + new string('x', 56)));
        Assert.Equal("emp 1042" + new string('x', 56), (string?)exact["input"]);
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
    public async Task EnrollChecksTheReferenceTheLabelAndTheImageUrlLocally()
    {
        // The length (1 to 64) first, then the pattern.
        var empty = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, ""));
        Same("""{"type":"string_too_short","loc":["body","reference"],"msg":"String should have at least 1 character","input":"","ctx":{"min_length":1}}""", empty);
        var tooLong = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, new string('r', 65)));
        Assert.Equal(("string_too_long", 64), ((string?)tooLong["type"], (int)tooLong["ctx"]!["max_length"]!));
        Assert.Equal(new string('r', 64) + "...", (string?)tooLong["input"]);
        var pattern = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, "emp 1042"));
        Same("""{"type":"string_pattern_mismatch","loc":["body","reference"],"msg":"String should match pattern '^[A-Za-z0-9_.:-]{1,64}$'","input":"emp 1042","ctx":{"pattern":"^[A-Za-z0-9_.:-]{1,64}$"}}""", pattern);
        var newline = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, "emp-1042\n"));
        Assert.Equal("string_pattern_mismatch", (string?)newline["type"]);
        var label = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, "emp-1042", new string('l', 129)));
        Assert.Equal(("string_too_long", """["body","label"]"""), ((string?)label["type"], label["loc"]!.ToJsonString()));
        Assert.Equal(new string('l', 64) + "...", (string?)label["input"]);
        // A label given but empty is too short; left out, it is not checked.
        var emptyLabel = await Refused(dn => dn.Collections.EnrollAsync(Id, Badge, "emp-1042", ""));
        Same("""{"type":"string_too_short","loc":["body","label"],"msg":"String should have at least 1 character","input":"","ctx":{"min_length":1}}""", emptyLabel);
        // A missing image's input is null.
        var noImage = await Refused(dn => dn.Collections.EnrollAsync(Id, null!, "emp-1042"));
        Same("""{"type":"missing","loc":["body","image"],"msg":"Field required","input":null}""", noImage);

        // Only a URL's rules are checked locally: a URL that is not https is refused, while a
        // picture the server cannot read is the server's 422 to answer.
        var stub = new StubHandler((_, _) => Json(201, """{"id":"itm_1"}"""));
        using var dn = Client(stub);
        await Assert.ThrowsAsync<BadRequestError>(() => dn.Collections.EnrollAsync(Id, Image.FromUrl("badge", "http://files.example.com/b.jpg"), "emp-1042"));
        Assert.Empty(stub.Requests);
        await dn.Collections.EnrollAsync(Id, Image.FromUploadId("badge", "upl_0000000000000000000000000000000a", "image/gif"), "emp-1042");
        Assert.Single(stub.Requests);
    }

    private const string Busy = """{"detail":{"error_type":"api_error","message":""}}""";

    private static string? Key(Recorded r) => r.Headers.TryGetValue("Idempotency-Key", out var v) ? v : null;

    [Fact]
    public async Task CreateAndEnrollCarryOneKeyAcrossTheirAttemptsAndANewOnePerCall()
    {
        var stub = new StubHandler((_, i) => i % 2 == 0 ? Json(500, Busy) : Json(201, Created));
        using var dn = Client(stub, maxRetries: 2);
        await dn.Collections.CreateAsync("Staff");
        await dn.Collections.EnrollAsync(Id, Badge, "emp-1042");
        var keys = stub.Requests.Select(Key).ToList();
        Assert.Equal(4, keys.Count);
        Assert.All(keys, k => Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", k!));
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(keys[2], keys[3]);
        Assert.NotEqual(keys[0], keys[2]);
    }

    [Fact]
    public async Task ReadsAndDeletesCarryNoKey()
    {
        var stub = new StubHandler((_, _) => Ok(Created));
        using var dn = Client(stub);
        await dn.Collections.GetAsync(Id);
        await dn.Collections.DeleteAsync(Id);
        await dn.Collections.ListItemsAsync(Id);
        await dn.Collections.DeleteItemAsync(Id, "itm_1");
        Assert.All(stub.Requests, r => Assert.Null(Key(r)));
    }

    [Fact]
    public async Task AnEnrollmentStillRunningIsRetriedAfterItsWait()
    {
        var stub = new StubHandler((_, i) => i == 0
            ? Json(409, """{"detail":{"error_type":"idempotency_in_progress","message":"A request with this key is running."}}""", ("retry-after-ms", "1"))
            : Json(201, """{"id":"itm_1"}"""));
        using var dn = Client(stub, maxRetries: 2);
        var item = await dn.Collections.EnrollAsync(Id, Badge, "emp-1042");
        Assert.Equal("itm_1", (string?)item["id"]);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Single(stub.Requests.Select(Key).Distinct());
    }

    [Fact]
    public async Task AReusedKeyIsTheBaseErrorAndNeverRetried()
    {
        var stub = new StubHandler((_, _) => Json(409, """{"detail":{"error_type":"idempotency_key_reused","message":"This key was used for another request."}}"""));
        using var dn = Client(stub, maxRetries: 2);
        var error = await Assert.ThrowsAsync<DecisionNodeError>(() => dn.Collections.EnrollAsync(Id, Badge, "emp-1042"));
        Assert.Equal(typeof(DecisionNodeError), error.GetType());
        Assert.Equal((409, "idempotency_key_reused"), (error.Status, error.ErrorType));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ReadsAreRetriedAsAnyCall()
    {
        var read = new StubHandler((_, i) => i == 0 ? Json(500, Busy) : Ok(Created));
        using var dn = Client(read, maxRetries: 2);
        await dn.Collections.GetAsync(Id);
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
