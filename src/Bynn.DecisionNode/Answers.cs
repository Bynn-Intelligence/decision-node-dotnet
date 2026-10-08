using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode;

/// <summary>
/// One answer of a decision: a <see cref="TruthAnswer"/>, <see cref="ChoiceAnswer"/>,
/// <see cref="ScoreAnswer"/>, <see cref="NumberAnswer"/>, <see cref="PointAnswer"/> or <see cref="BoxAnswer"/>. An answer of a type this version of the library does not
/// know, or of a known type with missing or wrongly typed fields, is an
/// <see cref="UnknownAnswer"/> holding its raw fields, so a new question type on the
/// server does not break an older client.
/// </summary>
public abstract class Answer
{
    internal Answer(JsonObject raw)
    {
        Raw = raw;
        Type = JsonValues.AsString(raw["type"]);
    }

    /// <summary>The answer type as sent: <c>truth</c>, <c>choice</c>, <c>score</c>, <c>number</c>, <c>point</c>, <c>box</c> or a newer one.</summary>
    public string? Type { get; }

    /// <summary>The answer's fields exactly as received.</summary>
    public JsonObject Raw { get; }

    /// <summary>This answer as a <see cref="TruthAnswer"/>.</summary>
    /// <exception cref="InvalidCastException">The answer is of another type.</exception>
    public TruthAnswer AsTruth() => As<TruthAnswer>();

    /// <summary>This answer as a <see cref="ChoiceAnswer"/>.</summary>
    /// <exception cref="InvalidCastException">The answer is of another type.</exception>
    public ChoiceAnswer AsChoice() => As<ChoiceAnswer>();

    /// <summary>This answer as a <see cref="ScoreAnswer"/>.</summary>
    /// <exception cref="InvalidCastException">The answer is of another type.</exception>
    public ScoreAnswer AsScore() => As<ScoreAnswer>();

    /// <summary>This answer as a <see cref="NumberAnswer"/>.</summary>
    /// <exception cref="InvalidCastException">The answer is of another type.</exception>
    public NumberAnswer AsNumber() => As<NumberAnswer>();

    /// <summary>This answer as a <see cref="PointAnswer"/>.</summary>
    /// <exception cref="InvalidCastException">The answer is of another type.</exception>
    public PointAnswer AsPoint() => As<PointAnswer>();

    /// <summary>This answer as a <see cref="BoxAnswer"/>.</summary>
    /// <exception cref="InvalidCastException">The answer is of another type.</exception>
    public BoxAnswer AsBox() => As<BoxAnswer>();

    private T As<T>() where T : Answer =>
        this as T ?? throw new InvalidCastException($"the answer is of type '{Type}', not {typeof(T).Name}");

    internal static Answer Parse(string id, JsonNode? node)
    {
        if (node is not JsonObject raw)
        {
            throw new DecisionNodeError($"answer '{id}' is not a JSON object");
        }
        try
        {
            return JsonValues.AsString(raw["type"]) switch
            {
                "truth" => new TruthAnswer(raw, Number(raw, "truth")),
                // The yes-or-no type under its older wire name, read the same.
                "noul" => new TruthAnswer(raw, Number(raw, "noul")),
                "number" => new NumberAnswer(
                    raw,
                    Number(raw, "number"),
                    Number(raw, "expected"),
                    Number(raw, "confidence"),
                    ChoiceProbabilities(raw)),
                "choice" => new ChoiceAnswer(
                    raw,
                    JsonValues.AsString(raw["choice"]) ?? throw new MalformedAnswer(),
                    Number(raw, "confidence"),
                    ChoiceProbabilities(raw)),
                "score" => new ScoreAnswer(
                    raw,
                    Number(raw, "score"),
                    Number(raw, "confidence"),
                    ScoreProbabilities(raw),
                    Legend(raw)),
                "point" => new PointAnswer(
                    raw,
                    Number(raw, "x"),
                    Number(raw, "y"),
                    Number(raw, "present"),
                    Number(raw, "confidence"),
                    Text(raw, "image"),
                    Number(raw, "width"),
                    Number(raw, "height")),
                "box" => new BoxAnswer(
                    raw,
                    Number(raw, "x1"),
                    Number(raw, "y1"),
                    Number(raw, "x2"),
                    Number(raw, "y2"),
                    Number(raw, "present"),
                    Number(raw, "confidence"),
                    Text(raw, "image"),
                    Number(raw, "width"),
                    Number(raw, "height")),
                _ => new UnknownAnswer(raw),
            };
        }
        catch (MalformedAnswer)
        {
            // A known type with a missing or wrongly typed field is kept, raw, as an
            // unknown answer; the decision does not fail.
            return new UnknownAnswer(raw);
        }
    }

    /// <summary>Raised inside <see cref="Parse"/> only; never leaves this class.</summary>
    private sealed class MalformedAnswer : Exception
    {
    }

    private static double Number(JsonObject raw, string field) =>
        JsonValues.TryDouble(raw[field], out var d) ? d : throw new MalformedAnswer();

    private static string Text(JsonObject raw, string field) =>
        JsonValues.AsString(raw[field]) ?? throw new MalformedAnswer();

    private static IReadOnlyDictionary<string, double> ChoiceProbabilities(JsonObject raw)
    {
        if (raw["probabilities"] is not JsonObject probs)
        {
            throw new MalformedAnswer();
        }
        var map = new Dictionary<string, double>(probs.Count, StringComparer.Ordinal);
        foreach (var (name, value) in probs)
        {
            map[name] = JsonValues.TryDouble(value, out var d) ? d : throw new MalformedAnswer();
        }
        return new ReadOnlyDictionary<string, double>(map);
    }

    private static IReadOnlyDictionary<int, double> ScoreProbabilities(JsonObject raw)
    {
        if (raw["probabilities"] is not JsonObject probs)
        {
            throw new MalformedAnswer();
        }
        var map = new SortedDictionary<int, double>();
        foreach (var (key, value) in probs)
        {
            map[Index(key)] = JsonValues.TryDouble(value, out var d) ? d : throw new MalformedAnswer();
        }
        return new ReadOnlyDictionary<int, double>(map);
    }

    private static IReadOnlyDictionary<int, JsonNode?> Legend(JsonObject raw)
    {
        if (raw["legend"] is not JsonObject legend)
        {
            throw new MalformedAnswer();
        }
        var map = new SortedDictionary<int, JsonNode?>();
        foreach (var (key, value) in legend)
        {
            map[Index(key)] = value?.DeepClone();
        }
        return new ReadOnlyDictionary<int, JsonNode?>(map);
    }

    private static int Index(string key) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : throw new MalformedAnswer();
}

/// <summary>
/// An answer of a type this version of the library does not know, or of a known type
/// whose fields are missing or of the wrong type. Its fields are in
/// <see cref="Answer.Raw"/> and its type name in <see cref="Answer.Type"/>.
/// </summary>
public sealed class UnknownAnswer : Answer
{
    internal UnknownAnswer(JsonObject raw)
        : base(raw)
    {
    }
}

/// <summary>
/// The answer to a <c>truth</c> question: the probability that the statement is true.
/// </summary>
public sealed class TruthAnswer : Answer
{
    internal TruthAnswer(JsonObject raw, double truth)
        : base(raw)
    {
        Truth = truth;
    }

    /// <summary>The probability that the statement is true, from 0 to 1.</summary>
    public double Truth { get; }
}

/// <summary>The answer to a <c>choice</c> question.</summary>
public sealed class ChoiceAnswer : Answer
{
    internal ChoiceAnswer(JsonObject raw, string choice, double confidence, IReadOnlyDictionary<string, double> probabilities)
        : base(raw)
    {
        Choice = choice;
        Confidence = confidence;
        Probabilities = probabilities;
    }

    /// <summary>The chosen option's name, byte for byte as sent.</summary>
    public string Choice { get; }

    /// <summary>How sure the answer is, from 0 to 1.</summary>
    public double Confidence { get; }

    /// <summary>The probability of each option, by option name, exactly as received.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; }
}

/// <summary>The answer to a <c>score</c> question.</summary>
public sealed class ScoreAnswer : Answer
{
    internal ScoreAnswer(
        JsonObject raw,
        double score,
        double confidence,
        IReadOnlyDictionary<int, double> probabilities,
        IReadOnlyDictionary<int, JsonNode?> legend)
        : base(raw)
    {
        Score = score;
        Confidence = confidence;
        Probabilities = probabilities;
        Legend = legend;
    }

    /// <summary>The expected level, from 0 to the number of levels minus one.</summary>
    public double Score { get; }

    /// <summary>How sure the answer is, from 0 to 1.</summary>
    public double Confidence { get; }

    /// <summary>The probability of each level, by level index.</summary>
    public IReadOnlyDictionary<int, double> Probabilities { get; }

    /// <summary>Each level index mapped to the level as it was sent.</summary>
    public IReadOnlyDictionary<int, JsonNode?> Legend { get; }
}

/// <summary>The answer to a <c>number</c> question.</summary>
public sealed class NumberAnswer : Answer
{
    internal NumberAnswer(JsonObject raw, double number, double expected, double confidence, IReadOnlyDictionary<string, double> probabilities)
        : base(raw)
    {
        Number = number;
        Expected = expected;
        Confidence = confidence;
        Probabilities = probabilities;
    }

    /// <summary>The most likely value of the grid, exactly as received.</summary>
    public double Number { get; }

    /// <summary>The expected value over the whole grid, as received.</summary>
    public double Expected { get; }

    /// <summary>The probability of <see cref="Number"/>, from 0 to 1.</summary>
    public double Confidence { get; }

    /// <summary>
    /// The probability of each listed value, keyed by the value exactly as the server wrote it
    /// (<c>"7"</c>, <c>"3.5"</c>), in the server's order. Values at 0.001 or below are not listed.
    /// </summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; }
}

/// <summary>
/// The answer to a <c>point</c> question: one spot in an image, from its top-left corner,
/// <c>X</c> to the right and <c>Y</c> down. Every number is as received: whole pixels, or
/// fractions of the width and height with <c>units: "normalized"</c>.
/// </summary>
public sealed class PointAnswer : Answer
{
    internal PointAnswer(JsonObject raw, double x, double y, double present, double confidence, string image, double width, double height)
        : base(raw)
    {
        X = x;
        Y = y;
        Present = present;
        Confidence = confidence;
        Image = image;
        Width = width;
        Height = height;
    }

    /// <summary>The spot's distance from the image's left edge.</summary>
    public double X { get; }

    /// <summary>The spot's distance from the image's top edge.</summary>
    public double Y { get; }

    /// <summary>
    /// The probability that what the instructions describe is in the image at all. Under a low
    /// value the place is only the best guess for something that is probably absent.
    /// </summary>
    public double Present { get; }

    /// <summary>The probability that the spot lies on the thing, if the thing is there.</summary>
    public double Confidence { get; }

    /// <summary>The id of the image looked in.</summary>
    public string Image { get; }

    /// <summary>The image's width in pixels.</summary>
    public double Width { get; }

    /// <summary>The image's height in pixels.</summary>
    public double Height { get; }
}

/// <summary>
/// The answer to a <c>box</c> question: a rectangle in an image, its top-left corner
/// (<c>X1</c>, <c>Y1</c>) and bottom-right corner (<c>X2</c>, <c>Y2</c>). Every number is as
/// received: whole pixels, or fractions of the width and height with <c>units: "normalized"</c>.
/// </summary>
public sealed class BoxAnswer : Answer
{
    internal BoxAnswer(JsonObject raw, double x1, double y1, double x2, double y2, double present, double confidence, string image, double width, double height)
        : base(raw)
    {
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        Present = present;
        Confidence = confidence;
        Image = image;
        Width = width;
        Height = height;
    }

    /// <summary>The left edge of the box.</summary>
    public double X1 { get; }

    /// <summary>The top edge of the box.</summary>
    public double Y1 { get; }

    /// <summary>The right edge of the box.</summary>
    public double X2 { get; }

    /// <summary>The bottom edge of the box.</summary>
    public double Y2 { get; }

    /// <summary>
    /// The probability that what the instructions describe is in the image at all. Under a low
    /// value the box is only the best guess for something that is probably absent.
    /// </summary>
    public double Present { get; }

    /// <summary>The probability that the box is right if the thing is there: that it overlaps the true box with an intersection over union of 0.5 or more.</summary>
    public double Confidence { get; }

    /// <summary>The id of the image looked in.</summary>
    public string Image { get; }

    /// <summary>The image's width in pixels.</summary>
    public double Width { get; }

    /// <summary>The image's height in pixels.</summary>
    public double Height { get; }
}
