using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bynn.DecisionNode.Internal;

/// <summary>
/// The API's fixed request rules, checked on this machine before any request. A fault
/// raises the class the API answers it with (<see cref="BadRequestError"/>, or
/// <see cref="ValidationError"/> where the API answers <c>422</c>), with no status and no
/// request id, so nothing is sent for a request the server would refuse. Token limits
/// stay the server's: they differ by model version.
/// </summary>
internal static class LocalChecks
{
    internal const int MaxImages = 4;
    internal const int MaxImageBytes = 10 * 1024 * 1024;
    internal const long MaxImagePixels = 40_000_000;
    internal const int MaxUrlChars = 2048;
    internal const int MaxChoiceOptions = 255;
    internal const int MaxScoreLevels = 10;
    internal const int MaxGridValues = 4096;

    /// <summary>Checks every question's fixed limits, in question order.</summary>
    internal static void Questions(JsonObject questions)
    {
        foreach (var (id, node) in questions)
        {
            if (node is not JsonObject q)
            {
                continue;
            }
            var criteria = q["criteria"];
            switch (JsonValues.AsString(q["type"]))
            {
                case "choice" when criteria is JsonObject options:
                    if (options.Count == 0)
                    {
                        throw BadRequest($"Choice question must have at least one choice: {id}");
                    }
                    if (options.Count > MaxChoiceOptions)
                    {
                        throw BadRequest($"Too many choices. Must have at most {MaxChoiceOptions} choices.");
                    }
                    break;
                case "score" when criteria is JsonArray or JsonObject:
                    var levels = criteria is JsonArray list ? list.Count : ((JsonObject)criteria).Count;
                    if (levels == 0)
                    {
                        throw Validation($"question {id}: a score needs at least one level");
                    }
                    if (levels > MaxScoreLevels)
                    {
                        throw BadRequest($"Too many score levels. Must have at most {MaxScoreLevels} levels.");
                    }
                    break;
                case "truth" or "noul":
                    var sides = criteria as JsonObject;
                    if (q["instructions"] is null && sides?["true"] is null && sides?["false"] is null)
                    {
                        throw BadRequest($"Truth question must have criteria or instructions: {id}");
                    }
                    break;
                case "number":
                    Number(id, q);
                    break;
            }
        }
    }

    private static void Number(string id, JsonObject q)
    {
        var low = Literal(q["min"], 0m, out var lowOk);
        var high = Literal(q["max"], 255m, out var highOk);
        var step = Literal(q["step"], 1m, out var stepOk);
        if (!lowOk || !highOk || !stepOk)
        {
            throw Validation($"question {id}: min, max and step must be numbers");
        }
        // A number beyond decimal's range is left to the server, which names the bound.
        if (low is { } lo && high is { } hi && step is { } st)
        {
            if (st <= 0)
            {
                throw Validation($"question {id}: step must be above 0, got {Text(st)}");
            }
            if (lo > hi)
            {
                throw Validation($"question {id}: min {Text(lo)} is above max {Text(hi)}");
            }
            decimal values;
            try
            {
                values = decimal.Floor((hi - lo) / st) + 1;
            }
            catch (OverflowException)
            {
                values = decimal.MaxValue;
            }
            if (values > MaxGridValues)
            {
                var count = values == decimal.MaxValue ? "more than 4096" : Text(values);
                throw Validation($"question {id}: the grid holds {count} values, the limit is {MaxGridValues}");
            }
        }
        var instructions = q["instructions"];
        if (instructions is null || JsonValues.AsString(instructions) == string.Empty)
        {
            throw BadRequest($"Number question must have instructions: {id}");
        }
    }

    /// <summary>
    /// The exact decimal of a JSON number from its text. <paramref name="ok"/> is false for a
    /// value that is not a number; the result is <c>null</c> for a number outside decimal's range.
    /// </summary>
    private static decimal? Literal(JsonNode? node, decimal fallback, out bool ok)
    {
        ok = true;
        if (node is null)
        {
            return fallback;
        }
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            ok = false;
            return null;
        }
        return decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static string Text(decimal d) => d.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>Checks the request's images: the count, the ids, and each image's own rules.</summary>
    internal static void Images(IReadOnlyList<Image> images)
    {
        if (images.Count > MaxImages)
        {
            throw BadRequest($"Too many images. Must have at most {MaxImages} images.", "api_usage_error");
        }
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            for (var j = 0; j < i; j++)
            {
                if (images[j].Id == image.Id)
                {
                    throw BadRequest($"Image id '{image.Id}' is used twice; image ids must be unique within a request.", "api_usage_error");
                }
            }
            if (image.Fault() is { } fault)
            {
                throw BadRequest(fault, "api_usage_error");
            }
        }
    }

    /// <summary>The <see cref="BadRequestError"/> the API answers a fault with, raised before any request.</summary>
    internal static BadRequestError BadRequest(string message, string? errorType = null) =>
        new(new ErrorDetails { Message = message, ErrorType = errorType });

    /// <summary>The <see cref="ValidationError"/> the API answers a fault with (a <c>422</c>), raised before any request.</summary>
    internal static ValidationError Validation(string message) =>
        new(new ErrorDetails { Message = message }, null);
}
