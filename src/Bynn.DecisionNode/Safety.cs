using System.Globalization;

namespace Bynn.DecisionNode;

/// <summary>
/// The result of the hosted API's safety check, read from the <c>x-decisionnode-safety</c>
/// response header. A self-hosted server sends no header, and then there is no
/// <see cref="Safety"/> at all.
/// </summary>
public sealed class Safety
{
    private Safety(bool isChecked, IReadOnlyDictionary<string, double?> scores, IReadOnlyList<string> flagged, string? reason)
    {
        Checked = isChecked;
        Scores = scores;
        Flagged = flagged;
        Reason = reason;
    }

    /// <summary>Whether the check ran.</summary>
    public bool Checked { get; }

    /// <summary>The <c>harm</c> score, from 0 to 1.</summary>
    public double? Harm => Scores.TryGetValue("harm", out var v) ? v : null;

    /// <summary>The <c>self_harm</c> score, from 0 to 1.</summary>
    public double? SelfHarm => Scores.TryGetValue("self_harm", out var v) ? v : null;

    /// <summary>Every category score in the header, in header order.</summary>
    public IReadOnlyDictionary<string, double?> Scores { get; }

    /// <summary>The categories above the API key's thresholds (flagged or blocked), in header order.</summary>
    public IReadOnlyList<string> Flagged { get; }

    /// <summary>Why the check did not run, when <see cref="Checked"/> is false.</summary>
    public string? Reason { get; }

    /// <summary>
    /// Parses the header: <c>harm=0.031, self_harm=0.004</c>, with an optional
    /// <c>;flagged</c> or <c>;blocked</c> parameter on a member, or
    /// <c>unchecked;reason="..."</c>. Returns <c>null</c> for a missing header.
    /// </summary>
    public static Safety? Parse(string? header)
    {
        if (header is null)
        {
            return null;
        }
        var value = header.Trim();
        if (value.StartsWith("unchecked", StringComparison.Ordinal))
        {
            string? reason = null;
            foreach (var param in value.Split(';').Skip(1))
            {
                var (k, v) = SplitPair(param);
                if (k == "reason")
                {
                    reason = Unquote(v);
                }
            }
            return new Safety(false, new Dictionary<string, double?>(), Array.Empty<string>(), reason);
        }

        var scores = new Dictionary<string, double?>();
        var flagged = new List<string>();
        foreach (var raw in value.Split(','))
        {
            var member = raw.Trim();
            if (member.Length == 0)
            {
                continue;
            }
            var parts = member.Split(';');
            var (name, number) = SplitPair(parts[0]);
            if (name.Length == 0)
            {
                continue;
            }
            scores[name] = double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            if (parts.Skip(1).Select(p => p.Trim()).Any(p => p is "flagged" or "blocked"))
            {
                flagged.Add(name);
            }
        }
        return new Safety(true, scores, flagged, null);
    }

    private static (string Key, string Value) SplitPair(string text)
    {
        var i = text.IndexOf('=');
        return i < 0 ? (text.Trim(), string.Empty) : (text[..i].Trim(), text[(i + 1)..].Trim());
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
}
