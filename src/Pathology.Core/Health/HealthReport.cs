using Pathology.Core.Detection;

namespace Pathology.Core.Health;

/// <summary>
/// One category's health: rated by its worst problem, never by a sum. Fixing the worst problems always shows,
/// and a pile of small ones can't outweigh one serious one.
/// </summary>
/// <param name="Problems">The category's problems (root-cause groups rated Low or worse), worst first.</param>
public sealed record CategoryHealth(FindingCategory Category, IReadOnlyList<FindingGroup> Problems)
{
    /// <summary>The worst problem's severity, or null when the category is clean.</summary>
    public Severity? Rating => Problems.Count == 0 ? null : Problems.Max(p => p.Severity);

    public bool IsClean => Problems.Count == 0;

    public int Count(Severity severity) => Problems.Count(p => p.Severity == severity);

    /// <summary>The problems holding the rating where it is: fix these and it drops.</summary>
    public IEnumerable<FindingGroup> Holding => Problems.Where(p => p.Severity == Rating);

    /// <summary>What the rating drops to once <see cref="Holding"/> is fixed, or null for clean.</summary>
    public Severity? AfterHolding => Problems.Where(p => p.Severity < Rating).Select(p => (Severity?)p.Severity).Max();
}

/// <summary>A PATH's health, category by category. There's deliberately no overall verdict.</summary>
/// <param name="Categories">Security, Correctness and Hygiene, in that order.</param>
/// <param name="Notes">Info findings: worth knowing (which tool wins, a stale Explorer PATH), never a problem.</param>
public sealed record HealthReport(IReadOnlyList<CategoryHealth> Categories, IReadOnlyList<FindingGroup> Notes)
{
    public CategoryHealth this[FindingCategory category] => Categories.First(c => c.Category == category);

    /// <summary>Nothing to fix in any category (notes don't count).</summary>
    public bool IsClean => Categories.All(c => c.IsClean);

    /// <summary>Problems at a severity, across every category.</summary>
    public int Count(Severity severity) => Categories.Sum(c => c.Count(severity));
}

/// <summary>Rates a diagnosis. Pure: the same groups always give the same report.</summary>
public static class HealthRater
{
    public static HealthReport Rate(Diagnosis diagnosis) => Rate(diagnosis.Groups);

    /// <param name="groups">Root-cause groups, worst first (as <see cref="Diagnoser"/> ranks them). A group counts once,
    /// in its worst finding's category, at its worst finding's severity.</param>
    public static HealthReport Rate(IReadOnlyList<FindingGroup> groups) => new(
        Enum.GetValues<FindingCategory>()
            .Select(c => new CategoryHealth(c, groups.Where(g => g.Severity > Severity.Info && g.Primary.Category == c).ToList()))
            .ToList(),
        groups.Where(g => g.Severity == Severity.Info).ToList());
}
