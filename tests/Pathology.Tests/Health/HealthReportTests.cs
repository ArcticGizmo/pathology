using Pathology.Core.Detection;
using Pathology.Core.Health;
using Pathology.Core.Model;
using Pathology.Tests.Detection;

namespace Pathology.Tests.Health;

public class HealthReportTests
{
    static int _next;

    static FindingGroup Problem(Severity severity, FindingCategory category = FindingCategory.Security, Finding? also = null)
    {
        var finding = new Finding
        {
            Rule = "TST-01", Category = category, Severity = severity, Subject = $"s{_next++}",
            RootCause = $"cause{_next}", Title = "t", What = "w", Why = "y", Fix = "f",
        };
        return new FindingGroup(finding.RootCause, finding, also is null ? [finding] : [finding, also]);
    }

    static HealthReport Rate(params FindingGroup[] groups) => HealthRater.Rate(groups.OrderByDescending(g => g.Severity).ToList());

    [Fact]
    public void Each_category_is_rated_by_its_worst_problem_not_a_sum()
    {
        var report = Rate(
            Problem(Severity.Medium),
            Problem(Severity.High),
            Problem(Severity.Low, FindingCategory.Correctness),
            Problem(Severity.Low, FindingCategory.Correctness),
            Problem(Severity.Low, FindingCategory.Correctness));

        Assert.Equal(Severity.High, report[FindingCategory.Security].Rating);
        // Three Lows are still Low: small problems never add up to a big one.
        Assert.Equal(Severity.Low, report[FindingCategory.Correctness].Rating);
        Assert.Null(report[FindingCategory.Hygiene].Rating);
        Assert.True(report[FindingCategory.Hygiene].IsClean);
    }

    [Fact]
    public void Categories_come_in_a_fixed_order()
    {
        Assert.Equal([FindingCategory.Security, FindingCategory.Correctness, FindingCategory.Hygiene], Rate().Categories.Select(c => c.Category));
    }

    [Fact]
    public void Nothing_to_fix_is_clean_everywhere_and_notes_do_not_count()
    {
        var report = Rate(Problem(Severity.Info), Problem(Severity.Info, FindingCategory.Correctness));

        Assert.True(report.IsClean);
        Assert.All(report.Categories, c => Assert.Null(c.Rating));
        Assert.Equal(2, report.Notes.Count);
    }

    [Fact]
    public void What_holds_a_rating_and_what_it_drops_to()
    {
        var report = Rate(Problem(Severity.High), Problem(Severity.High), Problem(Severity.Low));
        var security = report[FindingCategory.Security];

        Assert.Equal(2, security.Holding.Count());
        Assert.Equal(Severity.Low, security.AfterHolding);
    }

    [Fact]
    public void Fixing_the_only_problems_leaves_a_category_clean()
    {
        var security = Rate(Problem(Severity.Medium))[FindingCategory.Security];

        Assert.Single(security.Holding);
        Assert.Null(security.AfterHolding);
    }

    [Fact]
    public void A_problem_counts_once_in_its_worst_findings_category()
    {
        // A phantom directory (security) that's also a dead entry (correctness) is one security problem.
        var dead = new Finding
        {
            Rule = "COR-05", Category = FindingCategory.Correctness, Severity = Severity.Low, Subject = "x",
            RootCause = "missing:X", Title = "t", What = "w", Why = "y", Fix = "f",
        };
        var report = Rate(Problem(Severity.High, also: dead));

        Assert.Equal(Severity.High, report[FindingCategory.Security].Rating);
        Assert.True(report[FindingCategory.Correctness].IsClean);
    }

    [Fact]
    public void Counts_are_per_problem_across_categories()
    {
        var report = Rate(Problem(Severity.High), Problem(Severity.High, FindingCategory.Correctness), Problem(Severity.Low, FindingCategory.Hygiene));

        Assert.Equal(2, report.Count(Severity.High));
        Assert.Equal(1, report.Count(Severity.Low));
        Assert.Equal(0, report.Count(Severity.Medium));
    }

    [Fact]
    public void One_drive_root_cause_over_five_folders_is_one_problem()
    {
        var machine = new TestMachine(@"%SystemRoot%\system32;%SystemRoot%;C:\A;C:\B;C:\C;C:\D;C:\E") { Admin = false };
        foreach (var folder in new[] { "A", "B", "C", "D", "E" })
            machine.Folder($@"C:\{folder}", f => f.WritableByEveryone(inherited: true, sid: WellKnownSids.AuthenticatedUsers));

        var diagnosis = machine.Diagnose();
        var security = HealthRater.Rate(diagnosis)[FindingCategory.Security];

        // Five SEC-01s and the SEC-05 that explains them.
        Assert.Equal(6, diagnosis.Findings.Count(f => f.RootCause == @"inherited:C:\"));
        Assert.Equal(Severity.High, security.Rating);
        var only = Assert.Single(security.Problems);
        Assert.Equal("SEC-05", only.Primary.Rule);
        Assert.Null(security.AfterHolding);
    }

    [Fact]
    public void A_stock_Windows_PATH_is_clean()
    {
        var machine = new TestMachine(@"%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\System32\Wbem", @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps;")
            .Folder(@"C:\Windows\System32\Wbem")
            .Folder(@"C:\Users\you\AppData\Local\Microsoft\WindowsApps", f => f.WritableByYou());

        var report = HealthRater.Rate(machine.Diagnose());

        Assert.True(report.IsClean);
        Assert.NotEmpty(report.Notes);   // the WindowsApps UAC note
    }
}
