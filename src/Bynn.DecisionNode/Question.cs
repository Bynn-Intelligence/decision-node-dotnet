using System.Text.Json;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// One question in a decision, held in its wire form. Build it with
/// <see cref="Truth"/>, <see cref="Choice(object?, IEnumerable{string})"/>,
/// <see cref="Score"/>, <see cref="Number"/>, <see cref="Point"/> or <see cref="Box"/>, or pass the wire form itself (a <see cref="JsonObject"/>
/// converts implicitly), so a request copied from the documentation runs unchanged.
/// </summary>
/// <example>
/// <code>
/// using static Bynn.DecisionNode.Question;
///
/// var questions = new Dictionary&lt;string, Question&gt;
/// {
///     ["route"] = Choice("Where should this go?", new Dictionary&lt;string, string&gt;
///     {
///         ["billing"] = "money", ["bug"] = "broken", ["account"] = "login",
///     }),
///     ["urgency"] = Score("How urgent is this?", new[] { "routine", "today", "urgent", "critical" }),
///     ["escalate"] = Truth("Escalate to a human now?"),
/// };
/// </code>
/// </example>
public sealed class Question
{
    private readonly JsonObject _wire;

    private Question(JsonObject wire)
    {
        _wire = wire;
    }

    /// <summary>The question type as sent: <c>truth</c>, <c>choice</c>, <c>score</c>, <c>number</c>, <c>point</c>, <c>box</c>, or whatever a wire form names.</summary>
    public string? Type => JsonValues.AsString(_wire["type"]);

    /// <summary>A copy of the wire form of this question.</summary>
    public JsonObject ToJson() => (JsonObject)_wire.DeepClone();

    public override string ToString() => _wire.ToJsonString(JsonValues.Options);

    /// <summary>
    /// A yes-or-no question, answered as the probability that it is true.
    /// </summary>
    /// <param name="instructions">The question. A string or any JSON-serialisable value.</param>
    /// <param name="ifTrue">What counts as true. Sent as <c>criteria.true</c>.</param>
    /// <param name="ifFalse">What counts as false. Sent as <c>criteria.false</c>.</param>
    /// <remarks>The criteria are left out when neither side is given.</remarks>
    public static Question Truth(object? instructions = null, object? ifTrue = null, object? ifFalse = null)
    {
        var q = new JsonObject { ["type"] = "truth" };
        if (instructions is not null)
        {
            q["instructions"] = JsonValues.ToNode(instructions, "instructions");
        }
        if (ifTrue is not null || ifFalse is not null)
        {
            var criteria = new JsonObject();
            if (ifTrue is not null)
            {
                criteria["true"] = JsonValues.ToNode(ifTrue, "the true criterion");
            }
            if (ifFalse is not null)
            {
                criteria["false"] = JsonValues.ToNode(ifFalse, "the false criterion");
            }
            q["criteria"] = criteria;
        }
        return new Question(q);
    }

    /// <summary>One option of a set, given as a list of names (each sent with a <c>null</c> description).</summary>
    public static Question Choice(object? instructions, IEnumerable<string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var criteria = new JsonObject();
        foreach (var name in options)
        {
            ArgumentNullException.ThrowIfNull(name, nameof(options));
            criteria[name] = null;
        }
        return ChoiceFrom(instructions, criteria);
    }

    /// <summary>One option of a set, given as option names mapped to descriptions.</summary>
    public static Question Choice(object? instructions, IEnumerable<KeyValuePair<string, string>> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var criteria = new JsonObject();
        foreach (var (name, description) in options)
        {
            criteria[name] = description is null ? null : JsonValue.Create(description);
        }
        return ChoiceFrom(instructions, criteria);
    }

    /// <summary>
    /// One option of a set, given as option names mapped to descriptions of any
    /// JSON-serialisable shape (or <c>null</c>).
    /// </summary>
    public static Question Choice(object? instructions, IEnumerable<KeyValuePair<string, object?>> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var criteria = new JsonObject();
        foreach (var (name, description) in options)
        {
            criteria[name] = JsonValues.ToNode(description, $"the description of option '{name}'");
        }
        return ChoiceFrom(instructions, criteria);
    }

    private static Question ChoiceFrom(object? instructions, JsonObject criteria)
    {
        var q = new JsonObject { ["type"] = "choice" };
        if (instructions is not null)
        {
            q["instructions"] = JsonValues.ToNode(instructions, "instructions");
        }
        q["criteria"] = criteria;
        return new Question(q);
    }

    /// <summary>
    /// A place on an ordered scale. <paramref name="levels"/> run from low to high;
    /// each is a string or any JSON-serialisable value.
    /// </summary>
    public static Question Score(object? instructions, IEnumerable<object?> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var criteria = new JsonArray();
        var i = 0;
        foreach (var level in levels)
        {
            criteria.Add(JsonValues.ToNode(level, $"level {i}"));
            i++;
        }
        var q = new JsonObject { ["type"] = "score" };
        if (instructions is not null)
        {
            q["instructions"] = JsonValues.ToNode(instructions, "instructions");
        }
        q["criteria"] = criteria;
        return new Question(q);
    }

    /// <summary>
    /// A number on a grid from <paramref name="min"/> to <paramref name="max"/> in steps of
    /// <paramref name="step"/>, answered as the most likely value with a probability per value.
    /// An argument left out is left out of the wire form, so the server's defaults apply
    /// (0, 255 and 1). A grid holds at most 4,096 values.
    /// </summary>
    /// <param name="instructions">What to count or read. Required by the API.</param>
    /// <param name="min">The lowest value, at most 6 decimals.</param>
    /// <param name="max">The highest value the grid may reach; it need not be on the grid.</param>
    /// <param name="step">The spacing, above 0, at most 6 decimals.</param>
    /// <remarks>Each value is written in its shortest form: <c>5</c>, <c>0.5</c>, <c>-2.5</c>.</remarks>
    public static Question Number(object? instructions, double? min = null, double? max = null, double? step = null)
    {
        var q = new JsonObject { ["type"] = "number" };
        if (instructions is not null)
        {
            q["instructions"] = JsonValues.ToNode(instructions, "instructions");
        }
        if (min is { } lo)
        {
            q["min"] = NumberNode(lo, nameof(min));
        }
        if (max is { } hi)
        {
            q["max"] = NumberNode(hi, nameof(max));
        }
        if (step is { } st)
        {
            q["step"] = NumberNode(st, nameof(step));
        }
        return new Question(q);
    }

    /// <summary>
    /// One spot in an image, answered as <c>x</c> and <c>y</c> from the image's top-left corner,
    /// with the probability that the thing is there at all and how sure the place is.
    /// </summary>
    /// <param name="instructions">What to find: a phrase, a description with conditions, or a question. Required by the API.</param>
    /// <param name="image">The id of the image to look in, a string; required when the request carries two or more images. Left out when <c>null</c>. A reference by number ("Picture 2") goes in the instructions.</param>
    /// <param name="units"><c>pixels</c> (the default) or <c>normalized</c> (fractions of the width and height). Left out when <c>null</c>.</param>
    public static Question Point(object? instructions, string? image = null, string? units = null) =>
        Place("point", instructions, image, units);

    /// <summary>
    /// A rectangle in an image, answered as its top-left (<c>x1</c>, <c>y1</c>) and bottom-right
    /// (<c>x2</c>, <c>y2</c>) corners, with the probability that the thing is there at all and how
    /// sure the box is.
    /// </summary>
    /// <param name="instructions">What to find: a phrase, a description with conditions, or a question. Required by the API.</param>
    /// <param name="image">The id of the image to look in, a string; required when the request carries two or more images. Left out when <c>null</c>. A reference by number ("Picture 2") goes in the instructions.</param>
    /// <param name="units"><c>pixels</c> (the default) or <c>normalized</c> (fractions of the width and height). Left out when <c>null</c>.</param>
    public static Question Box(object? instructions, string? image = null, string? units = null) =>
        Place("box", instructions, image, units);

    private static Question Place(string type, object? instructions, string? image, string? units)
    {
        var q = new JsonObject { ["type"] = type };
        if (instructions is not null)
        {
            q["instructions"] = JsonValues.ToNode(instructions, "instructions");
        }
        if (image is not null)
        {
            q["image"] = image;
        }
        if (units is not null)
        {
            q["units"] = units;
        }
        return new Question(q);
    }

    private static JsonNode NumberNode(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "a number question's bounds and step must be finite");
        }
        // Whole values are written as integers (5, not 5.0); others in their shortest form.
        return value == Math.Floor(value) && Math.Abs(value) < 9.2e18
            ? JsonValue.Create((long)value)
            : JsonValue.Create(value);
    }

    /// <summary>A question from its wire form, sent exactly as given.</summary>
    public static Question FromJson(JsonObject wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return new Question((JsonObject)wire.DeepClone());
    }

    /// <summary>A question from its wire form as JSON text, sent exactly as given.</summary>
    public static Question FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"the question is not valid JSON: {ex.Message}", nameof(json), ex);
        }
        return node is JsonObject obj
            ? new Question(obj)
            : throw new ArgumentException("a question must be a JSON object", nameof(json));
    }

    /// <summary>A question from any JSON-serialisable value whose JSON form is an object.</summary>
    public static Question FromObject(object wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire is Question question)
        {
            return question;
        }
        return JsonValues.ToNode(wire, "the question") is JsonObject obj
            ? new Question(obj)
            : throw new ArgumentException("a question must serialise to a JSON object", nameof(wire));
    }

    public static implicit operator Question(JsonObject wire) => FromJson(wire);

    internal JsonObject Wire => _wire;
}
