using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Several places in one answer: a point or box question asked with max.</summary>
public class PlacesTests
{
    private static readonly Image Lot = Image.FromUrl("lot", "https://files.example.com/lot/0917.jpg");

    [Fact]
    public void MaxIsSentOnlyWhenGiven()
    {
        Assert.Equal("""{"type":"box","instructions":"Every car in the lot.","image":"lot","max":8}""", Question.Box("Every car in the lot.", "lot", max: 8).ToString());
        Assert.Equal("""{"type":"point","instructions":"Each person.","max":6}""", Question.Point("Each person.", max: 6).ToString());
        Assert.Equal("""{"type":"point","instructions":"Each person."}""", Question.Point("Each person.").ToString());
    }

    private static async Task<Decision> Answer(string answers)
    {
        using var dn = Client(new StubHandler((_, _) => Ok("""{"model":"decisionnode-moe-0.9.0-rc1","answers":""" + answers + "}")));
        return await dn.DecideAsync("s", new Dictionary<string, Question> { ["q"] = Question.Box("?", max: 8), ["t"] = Question.Truth("?") }, new[] { Lot });
    }

    [Fact]
    public async Task BoxesAndPointsAreReadInTheirOrderAndAsSent()
    {
        using var dn = Client(new StubHandler((_, _) => Ok("""
            {"answers":{
            "cars":{"type":"box","count":5,"present":0.99,"complete":0.94,"boxes":[{"x1":112,"y1":400,"x2":388,"y2":590,"confidence":0.91},{"x1":402,"y1":395,"x2":660,"y2":598,"confidence":0.90}],"image":"lot","width":1920,"height":1080},
            "people":{"type":"point","count":2,"present":0.97,"complete":0.99,"points":[{"x":0.3333,"y":0.4741,"confidence":0.88},{"x":0.6302,"y":0.4907,"confidence":0.85}],"image":"lot","width":1920,"height":1080},
            "none":{"type":"box","count":0,"present":0.02,"complete":1.0,"boxes":[],"image":"lot","width":1920,"height":1080}}}
            """)));
        var d = await dn.DecideAsync("s", new Dictionary<string, Question>
        {
            ["cars"] = Question.Box("Every car in the lot.", max: 8),
            ["people"] = Question.Point("Each person.", units: "normalized", max: 6),
            ["none"] = Question.Box("Every truck.", max: 4),
        }, new[] { Lot });
        var cars = d.Boxes("cars");
        Assert.Equal((5L, 0.99, 0.94, "lot", 1920.0, 1080.0), (cars.Count, cars.Present, cars.Complete, cars.Image, cars.Width, cars.Height));
        // The list can be shorter than the count: places that are the same thing are merged.
        Assert.Equal(new[] { new PlaceBox(112, 400, 388, 590, 0.91), new PlaceBox(402, 395, 660, 598, 0.90) }, cars.Boxes);
        var people = d.Points("people");
        Assert.Equal(new[] { new PlacePoint(0.3333, 0.4741, 0.88), new PlacePoint(0.6302, 0.4907, 0.85) }, people.Points);
        var none = d["none"].AsBoxes();
        Assert.Equal(0, none.Count);
        Assert.Empty(none.Boxes);
    }

    [Fact]
    public async Task TheShapeIsToldByTheAnswersOwnList()
    {
        // A one-place answer to a question asked with max is still a BoxAnswer.
        var d = await Answer("""{"q":{"type":"box","x1":1,"y1":2,"x2":3,"y2":4,"present":0.9,"confidence":0.8,"image":"lot","width":10,"height":10},"t":{"type":"truth","truth":0.5}}""");
        Assert.IsType<BoxAnswer>(d["q"]);
        // A points list under a box answer, and a boxes list under a point answer, are unknown.
        d = await Answer("""{"q":{"type":"box","count":1,"present":0.9,"complete":0.9,"points":[{"x":1,"y":2,"confidence":0.5}],"image":"lot","width":10,"height":10},"t":{"type":"truth","truth":0.5}}""");
        Assert.IsType<UnknownAnswer>(d["q"]);
        d = await Answer("""{"q":{"type":"point","count":1,"present":0.9,"complete":0.9,"boxes":[],"image":"lot","width":10,"height":10},"t":{"type":"truth","truth":0.5}}""");
        Assert.IsType<UnknownAnswer>(d["q"]);
    }

    [Theory]
    [InlineData("""{"type":"box","count":1.5,"present":0.9,"complete":0.9,"boxes":[],"image":"lot","width":10,"height":10}""")]
    [InlineData("""{"type":"box","count":-1,"present":0.9,"complete":0.9,"boxes":[],"image":"lot","width":10,"height":10}""")]
    [InlineData("""{"type":"box","count":true,"present":0.9,"complete":0.9,"boxes":[],"image":"lot","width":10,"height":10}""")]
    [InlineData("""{"type":"box","count":1,"present":0.9,"boxes":[],"image":"lot","width":10,"height":10}""")]
    [InlineData("""{"type":"box","count":1,"present":0.9,"complete":0.9,"boxes":{},"image":"lot","width":10,"height":10}""")]
    [InlineData("""{"type":"box","count":1,"present":0.9,"complete":0.9,"boxes":[{"x1":1,"y1":2,"x2":3,"y2":4}],"image":"lot","width":10,"height":10}""")]
    [InlineData("""{"type":"box","count":1,"present":0.9,"complete":0.9,"boxes":[{"x1":1,"y1":2,"x2":"3","y2":4,"confidence":0.5}],"image":"lot","width":10,"height":10}""")]
    [InlineData("""{"type":"box","count":1,"present":0.9,"complete":0.9,"boxes":[7],"image":"lot","width":10,"height":10}""")]
    [InlineData("""{"type":"box","count":1,"present":0.9,"complete":0.9,"boxes":[],"image":3,"width":10,"height":10}""")]
    [InlineData("""{"type":"point","count":1,"present":0.9,"complete":0.9,"points":[{"x":1,"confidence":0.5}],"image":"lot","width":10,"height":10}""")]
    public async Task AMalformedPlacesAnswerIsUnknown(string answer)
    {
        var d = await Answer("""{"q":""" + answer + ""","t":{"type":"truth","truth":0.4}}""");
        Assert.IsType<UnknownAnswer>(d["q"]);
        Assert.Equal(0.4, d.Truth("t").Truth);
    }

    private static JsonObject Entry(DecisionNodeError error) => (JsonObject)Assert.Single(((ValidationError)error).Entries!)!;

    [Theory]
    [InlineData("1", "greater_than_equal", "Input should be greater than or equal to 2", """{"ge":2}""")]
    [InlineData("17", "less_than_equal", "Input should be less than or equal to 16", """{"le":16}""")]
    [InlineData("2.5", "int_type", "Input should be a valid integer", null)]
    [InlineData("\"8\"", "int_type", "Input should be a valid integer", null)]
    [InlineData("true", "int_type", "Input should be a valid integer", null)]
    [InlineData("2.0", "int_type", "Input should be a valid integer", null)]
    public async Task AMaxOutside2To16IsTheApis422(string max, string type, string msg, string? ctx)
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var question = Question.FromJson("""{"type":"box","instructions":"Every car.","max":""" + max + "}");
        var error = await Assert.ThrowsAsync<ValidationError>(() => dn.DecideAsync("s", new Dictionary<string, Question> { ["cars"] = question }, new[] { Lot }));
        var entry = Entry(error);
        Assert.Equal(type, (string?)entry["type"]);
        Assert.Equal("""["body","questions","cars","box","max"]""", entry["loc"]!.ToJsonString());
        Assert.Equal(msg, (string?)entry["msg"]);
        Assert.Equal(max, entry["input"]!.ToJsonString());
        Assert.Equal(ctx, entry["ctx"]?.ToJsonString());
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task TwoAndSixteenAreSentAndTheCapabilityIsTheServers()
    {
        var stub = new StubHandler((_, _) => Ok("""{"answers":{"a":{"type":"truth","truth":0.5}}}"""));
        using var dn = Client(stub);
        await dn.DecideAsync("s", new Dictionary<string, Question> { ["a"] = Question.Point("?", max: 2), ["b"] = Question.Box("?", max: 16) }, new[] { Lot });
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task TheCheckRunsOnDecideManyBatchLinesAndASessionsOpen()
    {
        using var dn = Client(new StubHandler((_, _) => Ok()));
        var bad = new Dictionary<string, Question> { ["cars"] = Question.Box("Every car.", max: 1) };
        Assert.Throws<ValidationError>(() => dn.CheckRequest(new DecideRequest { Questions = bad, Images = new[] { Lot } }));
        await Assert.ThrowsAsync<ValidationError>(() => dn.DecideManyAsync(new[] { new DecideRequest { Questions = bad, Images = new[] { Lot } } }));
        await Assert.ThrowsAsync<ValidationError>(() => dn.Batches.CreateAsync(new[] { new BatchRequest { CustomId = "a", Questions = bad, Images = new[] { Lot } } }));
        // A session's open checks max like any question and leaves the rest to the server.
        Assert.Throws<ValidationError>(() => dn.CheckRequest(new SessionOptions { Questions = bad }));
        dn.CheckRequest(new SessionOptions { Questions = new Dictionary<string, Question> { ["cars"] = Question.Box("Every car.", max: 8) } });
    }
}
