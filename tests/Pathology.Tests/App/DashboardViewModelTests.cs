using Pathology.App.Rendering;
using Pathology.App.Scanning;
using Pathology.App.ViewModels;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Tests.App;

public class DashboardViewModelTests
{
    static DashboardViewModel Page(PathSnapshot snapshot, out RecordingNavigator nav, out PendingChanges pending) =>
        Page(Sessions.Showing(snapshot), out nav, out pending);

    static DashboardViewModel Page(ScanSession session, out RecordingNavigator nav, out PendingChanges pending)
    {
        nav = new RecordingNavigator();
        pending = new PendingChanges(session, new RecordingRepair());
        return new DashboardViewModel(session, nav, pending);
    }

    [Fact]
    public void Each_card_shows_its_rating_counts_and_what_it_takes_to_lower_it()
    {
        var vm = Page(PosedMachines.Messy(), out _, out _);

        Assert.Equal("Dashboard", vm.Title);
        var security = vm.Cards.Single(c => c.Name == "Security");
        Assert.Equal("HIGH", security.RatingWord);
        Assert.StartsWith("Fix ", security.HintLine);
        Assert.EndsWith("to bring it to Medium", security.HintLine);

        var hygiene = vm.Cards.Single(c => c.Name == "Hygiene");
        Assert.Equal("LOW", hygiene.RatingWord);
        Assert.EndsWith("to make it clean", hygiene.HintLine);
        Assert.False(vm.IsClean);
    }

    [Fact]
    public void Every_problem_is_listed_once_worst_first_with_its_fix_if_it_has_one()
    {
        var vm = Page(PosedMachines.Messy(), out _, out _);
        var problems = vm.Result!.Diagnosis.Groups.Where(g => g.Severity > Severity.Info).Select(g => g.RootCause).ToList();

        Assert.True(vm.HasToFix);
        Assert.All(vm.ToFix, i => Assert.True(i.HasFix));
        Assert.All(vm.WorthKnowing, i => Assert.False(i.HasFix));
        Assert.Equal(vm.ToFix.Select(i => i.Severity).OrderByDescending(s => s), vm.ToFix.Select(i => i.Severity));

        var listed = vm.ToFix.Concat(vm.WorthKnowing).Where(i => i.Group is { Severity: > Severity.Info }).Select(i => i.Group!.RootCause).ToList();
        Assert.Equal(problems.Order(), listed.Order());
    }

    [Fact]
    public void A_problem_a_move_settles_is_a_thing_to_fix()
    {
        var vm = Page(PosedMachines.Messy(), out _, out _);

        var writable = vm.ToFix.Single(i => i.Group?.Members.Any(f => f.Rule == "SEC-01") == true
                                            && i.Title.Contains("VS Code", StringComparison.Ordinal));
        Assert.StartsWith("Fix: Move ", writable.FixLine);
        Assert.DoesNotContain(vm.WorthKnowing, i => ReferenceEquals(i.Group, writable.Group));
    }

    [Fact]
    public void Opening_a_thing_to_fix_goes_to_its_entry_or_its_PATH()
    {
        var vm = Page(PosedMachines.Messy(), out var nav, out _);

        vm.ToFix.First(i => i.Group?.Members.Any(f => f.Rule == "SEC-01" && f.Entries.Any(e => e is { Scope: PathScope.Machine, Index: 0 })) == true)
            .ActivateCommand.Execute(null);
        vm.ToFix.Single(i => i.Fix!.Fix.Id == RemediationPlanner.WindowsFirstId).ActivateCommand.Execute(null);

        Assert.Equal((PathScope.Machine, 0), nav.Calls[0]);
        Assert.Equal(PathScope.Machine, nav.Calls[1]);
    }

    [Fact]
    public void A_problem_with_no_fix_unfolds_to_say_what_to_do()
    {
        var vm = Page(PosedMachines.Messy(), out var nav, out _);
        var item = vm.WorthKnowing.First();

        item.ActivateCommand.Execute(null);

        Assert.True(item.IsExpanded);
        Assert.True(item.HasAdvice);
        Assert.Empty(nav.Calls);
    }

    [Fact]
    public void A_staged_fix_says_so()
    {
        var vm = Page(PosedMachines.Messy(), out _, out var pending);
        Assert.DoesNotContain(vm.ToFix, i => i.IsStaged);

        pending.StageRecommended();

        Assert.Contains(vm.ToFix, i => i.IsStaged);
    }

    [Fact]
    public void A_stock_install_is_clean_with_nothing_to_fix()
    {
        var vm = Page(PosedMachines.Clean(), out _, out _);

        Assert.True(vm.IsClean);
        Assert.False(vm.HasToFix);
        Assert.All(vm.Cards, c =>
        {
            Assert.Equal("CLEAN", c.RatingWord);
            Assert.Equal("Nothing to fix", c.CountsLine);
        });
        Assert.All(vm.WorthKnowing, i => Assert.Equal(Severity.Info, i.Severity));
    }

    [Fact]
    public void An_empty_PATH_is_not_clean()
    {
        var vm = Page(PosedMachines.Empty(), out _, out _);

        Assert.Equal("HIGH", vm.Cards.Single(c => c.Name == "Correctness").RatingWord);
        Assert.False(vm.IsClean);
    }

    [Fact]
    public void The_worst_case_is_high_in_security_and_correctness()
    {
        var vm = Page(PosedMachines.Worst(), out _, out _);

        Assert.Equal("HIGH", vm.Cards.Single(c => c.Name == "Security").RatingWord);
        Assert.Equal("HIGH", vm.Cards.Single(c => c.Name == "Correctness").RatingWord);
    }

    [Fact]
    public void The_summary_line_says_when_and_how_long()
    {
        var vm = Page(PosedMachines.Messy(), out _, out _);

        Assert.StartsWith("Scanned ", vm.SummaryLine);
        Assert.Contains("2,047", vm.SummaryLine);
        Assert.NotEmpty(vm.ScanInfo);
    }

    [Fact]
    public void Nothing_scanned_shows_no_cards()
    {
        var vm = Page(Sessions.NeverScans(), out _, out _);

        Assert.Empty(vm.Cards);
        Assert.False(vm.HasToFix);
        Assert.True(vm.IsIdle);
        Assert.Equal("Scan", vm.RescanLabel);
    }
}
