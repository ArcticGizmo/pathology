using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Scoring;
using Pathology.Tests.Detection;

namespace Pathology.Tests.Scoring;

public class HealthScoreTests
{
    static int _next;

    static FindingGroup Problem(Severity severity, FindingCategory category = FindingCategory.Security, string? rootCause = null)
    {
        var finding = new Finding
        {
            Rule = "TST-01", Category = category, Severity = severity, Subject = $"s{_next++}",
            RootCause = rootCause ?? $"cause{_next}", Title = "t", What = "w", Why = "y", Fix = "f",
        };
        return new FindingGroup(finding.RootCause, finding, [finding]);
    }

    static HealthScore Score(params FindingGroup[] groups) =>
        HealthScorer.Score(groups.OrderByDescending(g => g.Severity).ToList());

    [Theory]
    [InlineData(Severity.Critical, 30)]
    [InlineData(Severity.High, 15)]
    [InlineData(Severity.Medium, 6)]
    [InlineData(Severity.Low, 2)]
    [InlineData(Severity.Info, 0)]
    public void The_weights_table(Severity severity, int points) => Assert.Equal(points, SeverityWeights.Points(severity));

    [Fact]
    public void The_category_weights_sum_to_one()
    {
        Assert.Equal(1.0, Enum.GetValues<FindingCategory>().Sum(SeverityWeights.CategoryWeight), precision: 9);
    }

    [Fact]
    public void Nothing_to_fix_is_a_perfect_100()
    {
        var score = Score(Problem(Severity.Info));

        Assert.Equal(100, score.Overall);
        Assert.Equal(HealthBand.Healthy, score.Band);
        Assert.Null(score.CappedBy);
        Assert.Null(score.Hint);
        Assert.Empty(score.Top);
    }

    [Fact]
    public void Any_real_problem_keeps_the_score_below_100()
    {
        var score = Score(Problem(Severity.Low, FindingCategory.Hygiene));

        Assert.Equal(98, score.Categories[FindingCategory.Hygiene]);
        Assert.Equal(99, score.Overall);   // 99.7, floored
    }

    [Fact]
    public void Sub_scores_weigh_in_at_50_35_15()
    {
        // Security 100 − 6, correctness 100 − 6 − 6 − 2, hygiene untouched.
        var score = Score(
            Problem(Severity.Medium, FindingCategory.Security),
            Problem(Severity.Medium, FindingCategory.Correctness),
            Problem(Severity.Medium, FindingCategory.Correctness),
            Problem(Severity.Low, FindingCategory.Correctness));

        Assert.Equal(94, score.Categories[FindingCategory.Security]);
        Assert.Equal(86, score.Categories[FindingCategory.Correctness]);
        Assert.Equal(100, score.Categories[FindingCategory.Hygiene]);
        Assert.Equal(92, score.Overall);   // 47 + 30.1 + 15 = 92.1
    }

    [Fact]
    public void Any_critical_caps_the_score_at_49()
    {
        var score = Score(Problem(Severity.Critical));

        Assert.Equal(85, score.Uncapped);   // security 70: 35 + 35 + 15
        Assert.Equal(49, score.Overall);
        Assert.Equal(Severity.Critical, score.CappedBy);
        Assert.Equal(HealthBand.AtRisk, score.Band);
    }

    [Fact]
    public void Any_high_caps_the_score_at_79()
    {
        var score = Score(Problem(Severity.High, FindingCategory.Correctness));

        Assert.Equal(94, score.Uncapped);   // 50 + 29.75 + 15
        Assert.Equal(79, score.Overall);
        Assert.Equal(Severity.High, score.CappedBy);
        Assert.Equal(HealthBand.Fair, score.Band);
    }

    [Fact]
    public void A_cap_that_does_not_bite_is_not_reported()
    {
        // Enough Highs that the weighted score is already under 79.
        var score = Score(Enumerable.Range(0, 5).Select(_ => Problem(Severity.High)).ToArray());

        Assert.Equal(62, score.Overall);   // security 25: 12.5 + 35 + 15 = 62.5
        Assert.Null(score.CappedBy);
    }

    [Fact]
    public void Sub_scores_clamp_at_zero()
    {
        var score = Score(Enumerable.Range(0, 6).Select(_ => Problem(Severity.Critical)).ToArray());

        Assert.Equal(0, score.Categories[FindingCategory.Security]);
        Assert.Equal(49, score.Overall);   // 0 + 35 + 15 = 50, capped
    }

    [Theory]
    [InlineData(100, HealthBand.Healthy)]
    [InlineData(90, HealthBand.Healthy)]
    [InlineData(89, HealthBand.Fair)]
    [InlineData(70, HealthBand.Fair)]
    [InlineData(69, HealthBand.NeedsAttention)]
    [InlineData(50, HealthBand.NeedsAttention)]
    [InlineData(49, HealthBand.AtRisk)]
    [InlineData(0, HealthBand.AtRisk)]
    public void Band_edges(int score, HealthBand band) => Assert.Equal(band, SeverityWeights.BandOf(score));

    [Fact]
    public void The_hint_names_the_fix_that_lifts_the_score_furthest()
    {
        var critical = Problem(Severity.Critical);
        var score = Score(critical, Problem(Severity.Medium, FindingCategory.Correctness), Problem(Severity.Low, FindingCategory.Hygiene));

        Assert.Same(critical, score.Hint!.Problem);
        Assert.Equal(97, score.Hint.Reaches);   // 50 + 32.9 + 14.7 = 97.6
    }

    [Fact]
    public void No_hint_when_no_single_fix_would_help()
    {
        // Two Criticals: fixing either leaves the other's cap in place.
        Assert.Null(Score(Problem(Severity.Critical), Problem(Severity.Critical)).Hint);
    }

    [Fact]
    public void Top_lists_real_problems_worst_first_and_counts_are_per_problem()
    {
        var score = HealthScorer.Score(
            [Problem(Severity.High), Problem(Severity.Medium), Problem(Severity.Low), Problem(Severity.Low), Problem(Severity.Info)],
            top: 3);

        Assert.Equal([Severity.High, Severity.Medium, Severity.Low], score.Top.Select(g => g.Severity));
        Assert.Equal(2, score.Count(Severity.Low));
        Assert.Equal(1, score.Count(Severity.Info));
    }

    [Fact]
    public void One_drive_root_cause_over_five_folders_costs_once()
    {
        // A standard user, so the UAC-exposure summary (a separate problem) stays out of the arithmetic.
        var machine = new TestMachine(@"%SystemRoot%\system32;%SystemRoot%;C:\A;C:\B;C:\C;C:\D;C:\E") { Admin = false };
        foreach (var folder in new[] { "A", "B", "C", "D", "E" })
            machine.Folder($@"C:\{folder}", f => f.WritableByEveryone(inherited: true, sid: WellKnownSids.AuthenticatedUsers));

        var diagnosis = machine.Diagnose();
        var score = HealthScorer.Score(diagnosis);

        // Five SEC-01s and the SEC-05 that explains them are one problem.
        Assert.Equal(6, diagnosis.Findings.Count(f => f.RootCause == @"inherited:C:\"));
        Assert.Equal(1, score.Count(Severity.Critical));
        Assert.Equal(70, score.Categories[FindingCategory.Security]);
        Assert.Equal(49, score.Overall);
        Assert.Equal("SEC-05", score.Hint!.Problem.Primary.Rule);
    }
}
