using Pathology.App.Rendering;
using Pathology.App.ViewModels;
using Pathology.Core.Detection;
using Pathology.Core.Model;

namespace Pathology.Tests.App;

public class FindingsViewModelTests
{
    static FindingsViewModel Page(out RecordingNavigator nav)
    {
        nav = new RecordingNavigator();
        return new FindingsViewModel(Sessions.Showing(PosedMachines.Messy()), nav);
    }

    [Fact]
    public void Problems_are_listed_worst_first_without_the_notes()
    {
        var vm = Page(out _);

        Assert.NotEmpty(vm.Rows);
        Assert.All(vm.Rows, r => Assert.NotEqual(Severity.Info, r.Group.Severity));
        Assert.Equal(vm.Rows.Select(r => r.Group.Severity).OrderByDescending(s => s), vm.Rows.Select(r => r.Group.Severity));
        Assert.Same(vm.Rows[0], vm.Selected);
        Assert.True(vm.Rows[0].IsSelected);
    }

    [Fact]
    public void Arriving_for_a_root_cause_selects_it()
    {
        var vm = Page(out _);
        var phantom = vm.Result!.Diagnosis.Groups.First(g => g.Members.Any(f => f.Rule == "SEC-03"));

        vm.Apply(new FindingsQuery(RootCause: phantom.RootCause));

        Assert.Equal(phantom.RootCause, vm.Selected?.Group.RootCause);
        Assert.Equal(phantom.Primary.Title, vm.Detail?.Finding.Title);
    }

    [Fact]
    public void Arriving_for_a_note_switches_to_the_notes()
    {
        var vm = Page(out _);
        var note = vm.Result!.Diagnosis.Groups.First(g => g.Severity == Severity.Info);

        vm.Apply(new FindingsQuery(RootCause: note.RootCause));

        Assert.Equal(SeverityFilter.Notes, vm.Severity.Value);
        Assert.Equal(note.RootCause, vm.Selected?.Group.RootCause);
    }

    [Fact]
    public void Filters_narrow_by_category_and_scope()
    {
        var vm = Page(out _);

        vm.Category = vm.CategoryOptions.First(o => o.Value == FindingCategory.Hygiene);
        Assert.NotEmpty(vm.Rows);
        Assert.All(vm.Rows, r => Assert.Equal(FindingCategory.Hygiene, r.Group.Primary.Category));
        Assert.StartsWith("Showing ", vm.CountLine);

        vm.Category = vm.CategoryOptions[0];
        vm.Scope = vm.ScopeOptions.First(o => o.Value == PathScope.User);
        Assert.All(vm.Rows, r => Assert.Contains(r.Group.Members, f => f.Scope == PathScope.User || f.Entries.Any(e => e.Scope == PathScope.User)));
    }

    [Fact]
    public void The_detail_shows_related_findings_and_copies_as_plain_text()
    {
        var vm = Page(out var nav);
        var row = vm.Rows.First(r => r.Group.Related.Any());
        vm.Selected = row;
        var detail = vm.Detail!;

        Assert.True(detail.HasRelated);
        Assert.Contains("What: ", detail.DetailsText);
        Assert.Contains("Fix: ", detail.DetailsText);

        detail.Related[0].OpenCommand.Execute(null);
        Assert.False(detail.IsShowingLead);
        detail.ShowLeadCommand.Execute(null);
        Assert.True(detail.IsShowingLead);

        detail.Entries[0].OpenCommand.Execute(null);
        Assert.IsType<ValueTuple<PathScope, int>>(nav.Calls.Single());
    }

    [Fact]
    public void Every_finding_with_a_learn_topic_links_to_a_real_article()
    {
        var vm = Page(out _);
        vm.Severity = vm.SeverityOptions.First(o => o.Value == SeverityFilter.Everything);

        foreach (var row in vm.Rows)
        {
            vm.Selected = row;
            if (row.Group.Primary.Learn is null) continue;
            Assert.True(vm.Detail!.HasLearn, row.Group.Primary.Rule);
            Assert.StartsWith("Learn: ", vm.Detail.LearnLabel);
        }
    }
}
