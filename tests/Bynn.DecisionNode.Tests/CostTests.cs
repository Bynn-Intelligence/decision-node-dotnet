using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

/// <summary>usage.cost: what each answer cost, read exactly as the API sent it.</summary>
public class CostTests
{
    private static async Task<Decision> Decide(string usage)
    {
        using var dn = Client(new StubHandler((_, _) => Ok("""{"model":"decisionnode-moe-0.9.0-rc1","answers":{"q":{"type":"truth","truth":0.5}},"usage":""" + usage + "}")));
        return await dn.DecideAsync("s", OneTruth());
    }

    [Fact]
    public async Task ABoostedAnswersCostIsReadAsSentNeverSummed()
    {
        var d = await Decide("""{"input_tokens":341,"output_tokens":0,"booster_tokens":72,"boosters":3,"cost":{"usd":0.036014322,"model_usd":0.000014322,"boosters_usd":0.036,"free_tier":false}}""");
        var cost = d.Usage.Cost!;
        Assert.Equal(0.036014322, cost.Usd);
        // The parts summed as doubles are not what the server booked.
        Assert.NotEqual(cost.ModelUsd!.Value + cost.BoostersUsd!.Value, cost.Usd!.Value);
        Assert.Equal(0.000014322, cost.ModelUsd);
        Assert.Equal(0.036, cost.BoostersUsd);
        Assert.False(cost.FreeTier);
        Assert.Equal(0.036014322, (double)cost.Raw!["usd"]!);
        Assert.Equal(new Cost(0.036014322, 0.000014322, 0.036, false), cost);
    }

    [Fact]
    public async Task AFreeTierAnswer()
    {
        var d = await Decide("""{"input_tokens":62,"output_tokens":0,"cost":{"usd":0,"model_usd":0,"boosters_usd":0,"free_tier":true}}""");
        Assert.Equal(new Cost(0, 0, 0, true), d.Usage.Cost);
    }

    [Fact]
    public async Task NoCostIsNullNeverZeros()
    {
        Assert.Null((await Decide("""{"input_tokens":62,"output_tokens":0}""")).Usage.Cost);
        Assert.Null((await Decide("""{"input_tokens":62,"output_tokens":0,"cost":null}""")).Usage.Cost);
        Assert.Null((await Decide("""{"input_tokens":62,"output_tokens":0,"cost":"0.1"}""")).Usage.Cost);
    }

    [Fact]
    public async Task AValueOfAnotherTypeIsNull()
    {
        var cost = (await Decide("""{"input_tokens":62,"output_tokens":0,"cost":{"usd":"0.1","model_usd":0.1,"free_tier":"no"}}""")).Usage.Cost!;
        Assert.Null(cost.Usd);
        Assert.Equal(0.1, cost.ModelUsd);
        Assert.Null(cost.BoostersUsd);
        Assert.Null(cost.FreeTier);
    }

    [Fact]
    public async Task ABatchResultLineCarriesItsOwnCost()
    {
        var file = """{"custom_id":"a","status":200,"body":{"answers":{"q":{"type":"truth","truth":0.5}},"usage":{"input_tokens":62,"output_tokens":0,"cost":{"usd":0.000001302,"model_usd":0.000001302,"boosters_usd":0,"free_tier":false}}},"headers":{}}""" + "\n";
        using var dn = Client(new StubHandler((_, _) => Json(200, file)));
        var results = new List<BatchResult>();
        await foreach (var r in dn.Batches.ResultsAsync("batch_01J9ZB8Q3T"))
        {
            results.Add(r);
        }
        Assert.Equal(new Cost(0.000001302, 0.000001302, 0, false), results[0].Decision!.Usage.Cost);
    }
}
