using Pathology.Core.Detection;

namespace Pathology.Core.Scoring;

/// <summary>"Fix this one problem and you'd reach <see cref="Reaches"/>%."</summary>
public sealed record ScoreHint(FindingGroup Problem, int Reaches);

/// <summary>A PATH's health: the overall score, how it was reached, and what would move it most.</summary>
/// <param name="Overall">0–100, after the caps.</param>
/// <param name="Uncapped">The weighted score before any cap applied.</param>
/// <param name="CappedBy">The severity whose cap lowered the score, or null when no cap bit.</param>
/// <param name="Categories">Each category's sub-score, 0–100.</param>
/// <param name="Problems">Problems (root-cause groups) by severity.</param>
/// <param name="Top">The worst problems, worst first.</param>
/// <param name="Hint">The single fix that would raise the score most, or null when none would.</param>
public sealed record HealthScore(
    int Overall,
    int Uncapped,
    Severity? CappedBy,
    IReadOnlyDictionary<FindingCategory, int> Categories,
    IReadOnlyDictionary<Severity, int> Problems,
    IReadOnlyList<FindingGroup> Top,
    ScoreHint? Hint)
{
    public HealthBand Band => SeverityWeights.BandOf(Overall);

    public int Count(Severity severity) => Problems.GetValueOrDefault(severity);
}

/// <summary>Scores a diagnosis. Pure: the same groups always give the same score.</summary>
public static class HealthScorer
{
    public const int DefaultTop = 5;

    public static HealthScore Score(Diagnosis diagnosis, int top = DefaultTop) => Score(diagnosis.Groups, top);

    /// <param name="groups">Root-cause groups, worst first (as <see cref="Diagnoser"/> ranks them).</param>
    public static HealthScore Score(IReadOnlyList<FindingGroup> groups, int top = DefaultTop)
    {
        var (overall, uncapped, cappedBy, categories) = Compute(groups);

        // The hint: which one problem, fixed on its own, lifts the score furthest (first in rank order on a tie).
        ScoreHint? hint = null;
        foreach (var group in groups.Where(g => SeverityWeights.Points(g.Severity) > 0))
        {
            var without = Compute(groups.Where(g => !ReferenceEquals(g, group)).ToList()).Overall;
            if (without > (hint?.Reaches ?? overall)) hint = new ScoreHint(group, without);
        }

        return new HealthScore(
            overall, uncapped, cappedBy, categories,
            Enum.GetValues<Severity>().ToDictionary(s => s, s => groups.Count(g => g.Severity == s)),
            groups.Where(g => g.Severity > Severity.Info).Take(top).ToList(),
            hint);
    }

    static (int Overall, int Uncapped, Severity? CappedBy, IReadOnlyDictionary<FindingCategory, int> Categories) Compute(IReadOnlyList<FindingGroup> groups)
    {
        // A problem costs once, against its worst finding's category and severity.
        var categories = Enum.GetValues<FindingCategory>().ToDictionary(
            c => c,
            c => Math.Max(0, 100 - groups.Where(g => g.Primary.Category == c).Sum(g => SeverityWeights.Points(g.Severity))));

        // Floored, so 100 means nothing at all to fix: one Low hygiene problem is 99.7, and shows as 99.
        var weighted = categories.Sum(p => p.Value * SeverityWeights.CategoryWeight(p.Key));
        var uncapped = (int)Math.Floor(weighted + 1e-9);

        var cap = groups.Any(g => g.Severity == Severity.Critical) ? (Severity?)Severity.Critical
            : groups.Any(g => g.Severity == Severity.High) ? Severity.High
            : null;
        var limit = cap switch
        {
            Severity.Critical => SeverityWeights.CriticalCap,
            Severity.High => SeverityWeights.HighCap,
            _ => 100,
        };
        var overall = Math.Min(uncapped, limit);
        return (overall, uncapped, overall < uncapped ? cap : null, categories);
    }
}
