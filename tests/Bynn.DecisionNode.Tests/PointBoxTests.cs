using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Point and box questions: the builders, the answers and the local checks.</summary>
public class PointBoxTests
{
    private static readonly Image Belt = Image.FromUrl("belt", "https://files.example.com/line-3/cam-2/0917.jpg");
    private static readonly Image Dock = Image.FromUrl("dock", "https://files.example.com/dock/0001.jpg");

    private const string Answered = """
        {"model":"decisionnode-1.0","answers":{
        "pick":{"type":"point","x":812,"y":455,"present":0.97,"confidence":0.91,"image":"belt","width":1920,"height":1080},
        "damage":{"type":"box","x1":0.1563,"y1":0.1111,"x2":0.2708,"y2":0.3796,"present":0.05,"confidence":0.88,"image":"belt","width":1920,"height":1080}}}
        """;

    [Fact]
    public void BuildersLeaveOutWhatIsNotGiven()
    {
        Assert.Equal("""{"type":"point","instructions":"Point to the yellow package."}""", Question.Point("Point to the yellow package.").ToString());
        Assert.Equal("""{"type":"box","instructions":"Draw a box.","image":"belt","units":"normalized"}""", Question.Box("Draw a box.", "belt", "normalized").ToString());
        Assert.Equal("""{"type":"point","instructions":"x","units":"pixels"}""", Question.Point("x", units: "pixels").ToString());
    }

    [Fact]
    public async Task AnswersAreReadAsReceived()
    {
        var stub = new StubHandler((_, _) => Ok(Answered));
        using var dn = Client(stub);
        var d = await dn.DecideAsync("s", new Dictionary<string, Question>
        {
            ["pick"] = Question.Point("Point to the yellow package closest to the robot arm."),
            ["damage"] = Question.Box("Draw a box around the damaged package.", units: "normalized"),
        }, new[] { Belt });

        var pick = d.Point("pick");
        Assert.Equal((812.0, 455.0, 0.97, 0.91, "belt", 1920.0, 1080.0), (pick.X, pick.Y, pick.Present, pick.Confidence, pick.Image, pick.Width, pick.Height));
        var damage = d["damage"].AsBox();
        Assert.Equal((0.1563, 0.1111, 0.2708, 0.3796, 0.05, 0.88), (damage.X1, damage.Y1, damage.X2, damage.Y2, damage.Present, damage.Confidence));
        Assert.Equal("belt", damage.Image);

        var sent = (JsonObject)Assert.Single(stub.Requests).Json!["questions"]!;
        Assert.Equal("point", (string?)sent["pick"]!["type"]);
        Assert.Equal("normalized", (string?)sent["damage"]!["units"]);
    }

    [Theory]
    [InlineData("""{"type":"point","x":true,"y":455,"present":0.97,"confidence":0.91,"image":"belt","width":1920,"height":1080}""")]
    [InlineData("""{"type":"point","x":812,"y":455,"present":0.97,"confidence":0.91,"image":"belt","width":1920}""")]
    [InlineData("""{"type":"point","x":812,"y":455,"present":0.97,"confidence":0.91,"image":7,"width":1920,"height":1080}""")]
    [InlineData("""{"type":"box","x1":"300","y1":120,"x2":520,"y2":410,"present":0.95,"confidence":0.88,"image":"belt","width":1920,"height":1080}""")]
    public async Task AMalformedPlaceIsUnknown(string answer)
    {
        var stub = new StubHandler((_, _) => Ok("""{"answers":{"q":""" + answer + ""","t":{"type":"truth","truth":0.4}}}"""));
        using var dn = Client(stub);
        var d = await dn.DecideAsync("s", new Dictionary<string, Question> { ["q"] = Question.Point("?"), ["t"] = Question.Truth("?") }, new[] { Belt });
        Assert.IsType<UnknownAnswer>(d["q"]);
        Assert.Equal(0.4, d.Truth("t").Truth);
        Assert.Throws<InvalidCastException>(() => d["q"].AsPoint());
    }

    private static async Task<ValidationError> Invalid(IReadOnlyDictionary<string, Question> questions, Image[]? images = null, Video[]? videos = null)
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<ValidationError>(() => dn.DecideAsync("s", questions, images, videos));
        Assert.Null(error.Status);
        Assert.Null(error.RequestId);
        Assert.Empty(stub.Requests);
        return error;
    }

    [Fact]
    public async Task APlaceWithoutAnImageIsRefused()
    {
        var error = await Invalid(new Dictionary<string, Question> { ["pick"] = Question.Point("Point to it.") });
        var entry = (JsonObject)Assert.Single(error.Entries!)!;
        Assert.Equal("image_required", (string?)entry["type"]);
        Assert.Equal("""["body","questions","pick","point"]""", entry["loc"]!.ToJsonString());
        Assert.Equal("A point question needs an image in the request", (string?)entry["msg"]);
        Assert.Equal("""{"type":"point","instructions":"Point to it."}""", entry["input"]!.ToJsonString());
    }

    [Fact]
    public async Task EveryFaultOfTheRequestIsListedInOrder()
    {
        var error = await Invalid(
            new Dictionary<string, Question>
            {
                ["pick"] = Question.Point("Point to it.", "crate", "inches"),
                ["any"] = Question.Truth("Is anything damaged?"),
                ["damage"] = Question.Box("Draw a box."),
                ["cam"] = Question.Point(new JsonObject { ["q"] = "x" }, "cam"),
                ["bad"] = new JsonObject { ["type"] = "box", ["instructions"] = 5, ["image"] = 0 },
            },
            new[] { Belt, Dock },
            new[] { Video.Of("cam", new[] { VideoFrame.FromBase64(0, "AAAA", "image/jpeg") }) });
        var entries = error.Entries!.Select(e => (string)e!["type"]! + " " + e["loc"]!.ToJsonString()).ToList();
        Assert.Equal(new[]
        {
            """unknown_image ["body","questions","pick","point","image"]""",
            """literal_error ["body","questions","pick","point","units"]""",
            """missing ["body","questions","damage","box","image"]""",
            """unknown_image ["body","questions","cam","point","image"]""",
            """string_type ["body","questions","bad","box","instructions","str"]""",
            """dict_type ["body","questions","bad","box","instructions","dict[any,any]"]""",
            """list_type ["body","questions","bad","box","instructions","list[any]"]""",
            """string_type ["body","questions","bad","box","image"]""",
        }, entries);
        var units = (JsonObject)error.Entries![1]!;
        Assert.Equal("Input should be 'pixels' or 'normalized'", (string?)units["msg"]);
        Assert.Equal("'pixels' or 'normalized'", (string?)units["ctx"]!["expected"]);
    }

    [Fact]
    public async Task OneImageNeedsNoImageFieldAndAnyNamedImagePasses()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("s", new Dictionary<string, Question> { ["pick"] = Question.Point("Point to it.") }, new[] { Belt });
        await dn.DecideAsync("s", new Dictionary<string, Question>
        {
            ["pick"] = Question.Point("Point to it.", "belt"),
            ["pallet"] = Question.Box("Draw a box.", "dock", "normalized"),
        }, new[] { Belt, Dock });
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task MissingInstructionsAreThePlainBadRequest()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync("s", new Dictionary<string, Question> { ["pick"] = Question.Point(null) }, new[] { Belt }));
        Assert.Equal("Point question must have instructions: pick", error.Message);
        Assert.Null(error.ErrorType);
        var box = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync("s", new Dictionary<string, Question> { ["d"] = Question.Box(null) }, new[] { Belt }));
        Assert.Equal("Box question must have instructions: d", box.Message);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task SystemoneRefusesPlacesAndSessionsAndBatchesCheckThem()
    {
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        var strict = await Assert.ThrowsAsync<BadRequestError>(() => dn.SystemoneAsync("s", new Dictionary<string, Question> { ["d"] = Question.Box("Draw a box.") }, new[] { Belt }));
        Assert.Equal("Invalid request.", strict.Message);
        Assert.Equal("api_usage_error", strict.ErrorType);

        // A session's images come with its frames, so the image rules wait for them.
        dn.CheckRequest(new SessionOptions { State = "Line 3.", Questions = new Dictionary<string, Question> { ["pick"] = Question.Point("Point to it.") } });

        var batch = Assert.Throws<ValidationError>(() => dn.Batches.CheckCreate(new[]
        {
            new BatchRequest { CustomId = "frame-1", Questions = new Dictionary<string, Question> { ["d"] = Question.Box("Draw a box.") } },
        }));
        Assert.Equal("image_required", (string?)batch.Entries![0]!["type"]);
        dn.Batches.CheckCreate(new[]
        {
            new BatchRequest { CustomId = "frame-1", Questions = new Dictionary<string, Question> { ["d"] = Question.Box("Draw a box.") }, Images = new[] { Belt } },
        });
        Assert.Empty(stub.Requests);
    }
}
