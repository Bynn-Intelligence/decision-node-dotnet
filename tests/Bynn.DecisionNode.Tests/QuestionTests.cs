using System.Text.Json.Nodes;
using static Bynn.DecisionNode.Question;

namespace Bynn.DecisionNode.Tests;

public class QuestionTests
{
    private static string Wire(Question q) => q.ToJson().ToJsonString();

    [Fact]
    public void TruthLeavesCriteriaOutWhenNeitherSideIsGiven()
    {
        Assert.Equal("""{"type":"truth","instructions":"Escalate?"}""", Wire(Truth("Escalate?")));
    }

    [Fact]
    public void TruthSendsOnlyTheSidesGiven()
    {
        Assert.Equal("""{"type":"truth","instructions":"Spam?","criteria":{"true":"bulk ads"}}""", Wire(Truth("Spam?", ifTrue: "bulk ads")));
        Assert.Equal("""{"type":"truth","criteria":{"true":"damaged","false":"fine"}}""", Wire(Truth(ifTrue: "damaged", ifFalse: "fine")));
    }

    [Fact]
    public void ChoiceFromNamesSendsNullDescriptions()
    {
        Assert.Equal("""{"type":"choice","instructions":"Currency?","criteria":{"SEK":null,"EUR":null}}""", Wire(Choice("Currency?", new[] { "SEK", "EUR" })));
    }

    [Fact]
    public void ChoiceKeepsOptionOrderAndNamesVerbatim()
    {
        var q = Choice("Which?", new Dictionary<string, string>
        {
            ["z"] = "last letter",
            [""] = "empty",
            [" padded "] = "spaces",
            ["Ünïcödé"] = "non-ASCII",
        });
        var criteria = (JsonObject)q.ToJson()["criteria"]!;
        Assert.Equal(new[] { "z", "", " padded ", "Ünïcödé" }, criteria.Select(p => p.Key).ToArray());
        Assert.Contains("Ünïcödé", q.ToString()); // written as UTF-8, not escaped
    }

    [Fact]
    public void ChoiceTakesStructuredDescriptions()
    {
        var q = Choice("Where?", new Dictionary<string, object?>
        {
            ["billing"] = null,
            ["bug"] = new { what = "something is broken", not_for = "billing" },
        });
        Assert.Equal("""{"type":"choice","instructions":"Where?","criteria":{"billing":null,"bug":{"what":"something is broken","not_for":"billing"}}}""", Wire(q));
    }

    [Fact]
    public void ScoreSendsLevelsLowToHigh()
    {
        Assert.Equal("""{"type":"score","instructions":"Urgent?","criteria":["routine","today","urgent"]}""", Wire(Score("Urgent?", new[] { "routine", "today", "urgent" })));
    }

    [Fact]
    public void ScoreTakesAnyJsonLevelsAndInstructions()
    {
        var q = Score(new { question = "How risky?", focus = "command" }, new object?[] { new { what = "no risk" }, new[] { "high", "risk" }, "critical" });
        Assert.Equal("""{"type":"score","instructions":{"question":"How risky?","focus":"command"},"criteria":[{"what":"no risk"},["high","risk"],"critical"]}""", Wire(q));
    }

    [Fact]
    public void WireFormIsAcceptedUnchanged()
    {
        Question q = new JsonObject { ["type"] = "truth", ["instructions"] = "Escalate?", ["criteria"] = new JsonObject { ["true"] = "yes", ["false"] = null } };
        Assert.Equal("""{"type":"truth","instructions":"Escalate?","criteria":{"true":"yes","false":null}}""", Wire(q));
        Assert.Equal("truth", q.Type);
        Assert.Equal(Wire(q), Wire(FromJson("""{"type":"truth","instructions":"Escalate?","criteria":{"true":"yes","false":null}}""")));
    }

    [Fact]
    public void WireFormMustBeAnObject()
    {
        Assert.Throws<ArgumentException>(() => FromJson("[1,2]"));
        Assert.Throws<ArgumentException>(() => FromJson("{not json"));
    }

    [Fact]
    public void UnserialisableValueFailsBeforeSending()
    {
        var loop = new Node();
        loop.Next = loop;
        Assert.Throws<ArgumentException>(() => Truth(loop));
    }

    private sealed class Node
    {
        public Node? Next { get; set; }
    }
}
