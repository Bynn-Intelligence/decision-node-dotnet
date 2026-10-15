using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>Depth Vision (<c>bynn_depth</c>) and Segment Vision (<c>bynn_segment</c>): their names, options and readings.</summary>
public class VisionReadingTests
{
    [Fact]
    public async Task TheirNamesAndOptionsAreSentAsGivenWithNothingCheckedLocally()
    {
        Assert.Equal(("bynn_depth", "bynn_segment"), (Booster.BynnDepth, Booster.BynnSegment));
        var stub = new StubHandler((_, _) => Ok());
        using var dn = Client(stub);
        await dn.DecideAsync("s", OneTruth(), new[] { Image.FromUrl("a1b2", "https://files.example.com/a1b2.jpg") }, boosters: new Booster[]
        {
            Booster.Of(Booster.BynnDepth, options: new { grid_size = 99 }),
            Booster.Of(Booster.BynnSegment, options: new { prompt = new[] { "purple gate", "landing pad", "", "x", "y" }, max_instances = 0, depth = false, colour = "red" }),
            Booster.BynnSegment,
        });
        Assert.Equal(
            """[{"name":"bynn_depth","options":{"grid_size":99}},{"name":"bynn_segment","options":{"prompt":["purple gate","landing pad","","x","y"],"max_instances":0,"depth":false,"colour":"red"}},"bynn_segment"]""",
            Assert.Single(stub.Requests).Json!["boosters"]!.ToJsonString());
    }

    [Fact]
    public async Task DepthReadingsAreTyped()
    {
        const string body = """
            {"answers":{},"evidence":{
              "bynn_depth":{"version":"bynn_depth-v1","status":"ok","latency_ms":60,"billed_calls":3,"results":[
                {"image":"a1b2","ahead":3.21,"directions":[5.12,4.03,null,2.81,6.02,7.15,9.33],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1.01,2.5],[14.98,0.30000000000000004]],"depth_min":1.01,"depth_max":14.98,"depth_median":4.37,"width":810,"height":1080,"units":"meters"},
                {"image":"sky","ahead":null,"directions":[null,null,null,null,null,null,null],"ground":false,"grid_rows":2,"grid_cols":2,"grid":[[900,1000],[1000,1000]],"depth_min":900,"depth_max":1000,"depth_median":1000,"width":64,"height":96},
                {"image":"c","directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2.5,"width":2,"height":2},
                {"image":"x","readable":false},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7,8],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,"4",5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":null,"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":"1","directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"ground":"true","grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2.0,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":-2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,null]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],3],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_max":4,"depth_median":2,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":null,"width":2,"height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":"2","height":2},
                {"image":"x","ahead":1,"directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2},
                {"image":5,"ahead":1,"directions":[1,2,3,4,5,6,7],"ground":true,"grid_rows":2,"grid_cols":2,"grid":[[1,2],[3,4]],"depth_min":1,"depth_max":4,"depth_median":2,"width":2,"height":2}]}}}
            """;
        using var dn = Client(new StubHandler((_, _) => Ok(body)));
        var results = (await dn.DecideAsync("s", OneTruth())).Evidence!["bynn_depth"].Results;

        var near = Assert.IsType<DepthReading>(results[0]);
        Assert.Equal(("a1b2", "bynn_depth", 3.21, true), (near.Image, near.Booster, near.Ahead, near.Ground));
        Assert.Equal(new double?[] { 5.12, 4.03, null, 2.81, 6.02, 7.15, 9.33 }, near.Directions);
        Assert.Equal((2L, 2L, 810L, 1080L), (near.GridRows, near.GridCols, near.Width, near.Height));
        // Every number as sent, never rounded.
        Assert.Equal(new[] { 1.01, 2.5 }, near.Grid[0]);
        Assert.Equal(new[] { 14.98, 0.30000000000000004 }, near.Grid[1]);
        Assert.Equal((1.01, 14.98, 4.37), (near.DepthMin, near.DepthMax, near.DepthMedian));

        var sky = Assert.IsType<DepthReading>(results[1]);
        Assert.Null(sky.Ahead);
        Assert.False(sky.Ground);
        Assert.All(sky.Directions, d => Assert.Null(d));
        Assert.Equal(7, sky.Directions.Count);

        // No ahead at all reads as none found.
        Assert.Null(Assert.IsType<DepthReading>(results[2]).Ahead);

        // An unreadable picture, directions of 6 or 8 entries or with a string or not a list, an
        // ahead that is not a number, a ground that is not a boolean or is missing, a fractional
        // or negative grid side, a grid cell that is null or a row that is not a list, no grid,
        // a missing or null depth summary, a width that is not a number or a missing height, and
        // an image id that is not a string: generic readings.
        Assert.All(results.Skip(3), r => Assert.IsType<UnknownReading>(r));
        Assert.Equal("x", results[3].Image);
    }

    [Fact]
    public async Task SegmentReadingsAreTyped()
    {
        const string body = """
            {"answers":{},"evidence":{
              "bynn_segment":{"version":"bynn_segment-v1","status":"ok","latency_ms":60,"billed_calls":3,"results":[
                {"image":"a1b2","width":1280,"height":720,"prompts":["purple gate","landing pad"],"instances":[
                  {"label":"purple gate","score":0.912,"box":[0.3125,0.2222,0.5781,0.8056],"polygon":[[0.3125,0.2222],[0.5781,0.2222],[0.5781,0.8056]],"area":0.12345,"centroid":[0.4512,0.4701],
                   "opening":{"polygon":[[0.4,0.3],[0.5,0.3],[0.5,0.5]],"centroid":[0.44,0.38],"width":0.1,"height":0.2,"offset":[-0.06,-0.12],"area":0.02},
                   "depth":{"median":4.2,"opening_median":10.5,"opening_rim_median":null},
                   "size_m":{"width":1.6,"height":2.1,"opening":{"width":0.8,"height":1.2}},"note":"x"},
                  {"label":"landing pad","score":0.3,"box":[0,0,1,1],"polygon":[[0.5,0.5]],"area":1,"centroid":[0.5,0.5],"opening":null,"depth":{"median":2.5},"size_m":{"width":3,"height":3,"opening":null}},
                  {"label":"landing pad","score":0.26,"box":[0.1,0.1,0.2,0.2],"polygon":[[0.1,0.1]],"area":0.01,"centroid":[0.15,0.15]}],
                 "depth_source":"bynn_depth","assumed_hfov_deg":70.0,"model":"x"},
                {"image":"off","width":96,"height":64,"prompts":["cat"],"instances":[],"depth_source":null,"assumed_hfov_deg":null},
                {"image":"bare","width":96,"height":64,"prompts":["cat"],"instances":[]},
                {"image":"x","readable":false},
                {"image":"x","width":96.5,"height":64,"prompts":["cat"],"instances":[]},
                {"image":"x","width":96,"height":64,"prompts":"cat","instances":[]},
                {"image":"x","width":96,"height":64,"prompts":["cat",3],"instances":[]},
                {"image":"x","width":96,"height":64,"prompts":["cat"]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[7]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[],"depth_source":5},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[],"assumed_hfov_deg":"70"},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5]}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":"0.5","box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5]}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5]}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,null],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5]}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":{"x1":0,"y1":0,"x2":1,"y2":1},"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5]}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0,0]],"area":1,"centroid":[0.5,0.5]}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"area":1,"centroid":[0.5,0.5]}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"centroid":[0.5,0.5]}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"area":1}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5],"opening":"none"}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5],"opening":{"polygon":[[0,0]],"centroid":[0.5,0.5],"width":0.1,"height":0.1,"area":0.01}}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5],"depth":{"median":"4"}}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5],"depth":[4]}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5],"size_m":{"width":1}}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5],"size_m":{"width":1,"height":1,"opening":{"height":1}}}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[{"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5],"size_m":{"width":1,"height":1,"opening":2}}]},
                {"image":"x","width":96,"height":64,"prompts":["cat"],"instances":[
                  {"label":"cat","score":0.5,"box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5,0.5]},
                  {"label":"cat","score":0.4,"box":[0,0,1,1],"polygon":[[0,0]],"area":1,"centroid":[0.5]}]}]}}}
            """;
        using var dn = Client(new StubHandler((_, _) => Ok(body)));
        var results = (await dn.DecideAsync("s", OneTruth())).Evidence!["bynn_segment"].Results;

        var gate = Assert.IsType<SegmentReading>(results[0]);
        Assert.Equal(("a1b2", "bynn_segment", 1280L, 720L), (gate.Image, gate.Booster, gate.Width, gate.Height));
        Assert.Equal(new[] { "purple gate", "landing pad" }, gate.Prompts);
        Assert.Equal(("bynn_depth", (double?)70.0), (gate.DepthSource, gate.AssumedHfovDeg));
        // The instances in the order sent.
        Assert.Equal(new[] { 0.912, 0.3, 0.26 }, gate.Instances.Select(i => i.Score));

        var first = gate.Instances[0];
        Assert.Equal(("purple gate", 0.12345), (first.Label, first.Area));
        Assert.Equal(new SegmentBox(0.3125, 0.2222, 0.5781, 0.8056), first.Box);
        Assert.Equal(new[] { new SegmentPoint(0.3125, 0.2222), new SegmentPoint(0.5781, 0.2222), new SegmentPoint(0.5781, 0.8056) }, first.Polygon);
        Assert.Equal(new SegmentPoint(0.4512, 0.4701), first.Centroid);
        var opening = first.Opening!;
        Assert.Equal(3, opening.Polygon.Count);
        Assert.Equal((new SegmentPoint(0.44, 0.38), 0.1, 0.2, new SegmentPoint(-0.06, -0.12), 0.02), (opening.Centroid, opening.Width, opening.Height, opening.Offset, opening.Area));
        Assert.Equal(new SegmentDepth(4.2, 10.5, null), first.Depth);
        Assert.Equal(new SegmentSize(1.6, 2.1, new SegmentOpeningSize(0.8, 1.2)), first.SizeM);

        // A null opening, a depth with only its median, a size without an opening.
        var pad = gate.Instances[1];
        Assert.Null(pad.Opening);
        Assert.Equal(new SegmentDepth(2.5, null, null), pad.Depth);
        Assert.Equal(new SegmentSize(3, 3, null), pad.SizeM);

        // Absent, the three are null.
        var plain = gate.Instances[2];
        Assert.Equal((null, null, null), (plain.Opening, plain.Depth, plain.SizeM));

        // Depth off: no instances, and the source and field of view null, or absent.
        var off = Assert.IsType<SegmentReading>(results[1]);
        Assert.Empty(off.Instances);
        Assert.Equal((null, null), (off.DepthSource, off.AssumedHfovDeg));
        var bare = Assert.IsType<SegmentReading>(results[2]);
        Assert.Equal((null, null), (bare.DepthSource, bare.AssumedHfovDeg));

        // An unreadable picture, a fractional side, prompts that are not a list of strings, no
        // instances or an instance that is not an object, a depth source or field of view of the
        // wrong type, an instance missing its label, polygon, area or centroid, a score that is
        // not a number, a box of 3 numbers, with a null or as an object, a point of 3 numbers, an
        // opening of the wrong type or without its offset, a depth or size of the wrong shape,
        // and one wrongly typed instance among good ones: generic readings.
        Assert.All(results.Skip(3), r => Assert.IsType<UnknownReading>(r));
    }
}
