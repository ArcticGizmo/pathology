using Pathology.Core.Detection;

namespace Pathology.Core.Scoring;

/// <summary>The health band a score falls in, worst last.</summary>
public enum HealthBand
{
    /// <summary>90–100.</summary>
    Healthy,

    /// <summary>70–89.</summary>
    Fair,

    /// <summary>50–69.</summary>
    NeedsAttention,

    /// <summary>0–49.</summary>
    AtRisk,
}

/// <summary>
/// Every number the health score is made of, in one place so tuning it is one edit (and one set of tests).
/// Points are charged per <i>problem</i> (a root-cause group), at its worst finding's severity.
/// </summary>
public static class SeverityWeights
{
    public static int Points(Severity severity) => severity switch
    {
        Severity.Critical => 30,
        Severity.High => 15,
        Severity.Medium => 6,
        Severity.Low => 2,
        _ => 0,
    };

    /// <summary>How much each category's sub-score counts toward the overall score (they sum to 1).</summary>
    public static double CategoryWeight(FindingCategory category) => category switch
    {
        FindingCategory.Security => 0.50,
        FindingCategory.Correctness => 0.35,
        _ => 0.15,
    };

    /// <summary>Any Critical problem caps the overall score here, however clean the rest is.</summary>
    public const int CriticalCap = 49;

    /// <summary>Any High problem caps the overall score here.</summary>
    public const int HighCap = 79;

    public static HealthBand BandOf(int score) => score switch
    {
        >= 90 => HealthBand.Healthy,
        >= 70 => HealthBand.Fair,
        >= 50 => HealthBand.NeedsAttention,
        _ => HealthBand.AtRisk,
    };
}
