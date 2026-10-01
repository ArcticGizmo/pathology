using Pathology.App;
using Pathology.App.Rendering;
using Pathology.App.ViewModels;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Tests.App;

public class ShellTests
{
    static MainWindowViewModel Shell(AppServices services) =>
        new(services, Sessions.Showing(PosedMachines.Messy()), new PosedRepair(), checkForUpdates: false);

    [Fact]
    public void The_nav_is_the_dashboard_the_two_PATHs_under_Entries_shadowing_and_history()
    {
        using var store = new TempStore();
        using var services = new AppServices(store.Root);
        var vm = Shell(services);

        var labels = vm.NavItems.Select(i => i is NavGroupViewModel g ? "+" + g.Label : ((PageViewModel)i).Title).ToList();
        Assert.Equal(["Dashboard", "+Entries", "System", "User", "Shadowing", "History"], labels);
        Assert.Same(vm.Dashboard, vm.CurrentPage);
        // Learn is a bonus: it sits with Settings and About, and Review is reached from the pending bar.
        Assert.DoesNotContain(vm.Learn, vm.NavItems);
        Assert.DoesNotContain(vm.Review, vm.NavItems);
        Assert.Contains(vm.Review, vm.Pages);
    }

    [Fact]
    public void The_entries_badges_count_entries_with_a_problem()
    {
        using var store = new TempStore();
        using var services = new AppServices(store.Root);
        var vm = Shell(services);
        var result = vm.Session.Current!;

        foreach (var (page, scope) in new[] { (vm.SystemEntries, PathScope.Machine), (vm.UserEntries, PathScope.User) })
            Assert.Equal(result.Snapshot.EntriesIn(scope).Count(e => result.GroupsFor(e).Any(g => g.Severity > Severity.Info)), page.NavCount);
    }

    [Fact]
    public void The_Entries_group_opens_System_and_reads_as_open_while_either_page_shows()
    {
        using var store = new TempStore();
        using var services = new AppServices(store.Root);
        var vm = Shell(services);
        var group = vm.NavItems.OfType<NavGroupViewModel>().Single();
        Assert.False(group.IsActive);

        vm.OpenGroupCommand.Execute(group);
        Assert.Same(vm.SystemEntries, vm.CurrentPage);
        Assert.True(group.IsActive);

        vm.NavigateCommand.Execute(vm.UserEntries);
        Assert.True(group.IsActive);

        vm.NavigateCommand.Execute(vm.History);
        Assert.False(group.IsActive);
    }

    [Fact]
    public void A_move_staged_on_System_shows_on_User_and_in_Review()
    {
        using var store = new TempStore();
        using var services = new AppServices(store.Root);
        var vm = Shell(services);

        vm.ToEntry(PathScope.Machine, 0);
        vm.SystemEntries.Selected!.MoveScopeCommand.Execute(null);

        Assert.Contains(vm.UserEntries.Rows, r => r.Text == @"C:\Tools" && !r.IsGhost);
        vm.SystemEntries.ReviewCommand.Execute(null);
        Assert.Same(vm.Review, vm.CurrentPage);
        Assert.Contains("written first", vm.Review.Plan!.AdminLine);

        vm.Review.BackCommand.Execute(null);
        Assert.Contains(vm.CurrentPage, new PageViewModel[] { vm.SystemEntries, vm.UserEntries });
    }

    [Fact]
    public void Navigating_lands_on_the_page_with_the_thing_selected()
    {
        using var store = new TempStore();
        using var services = new AppServices(store.Root);
        var vm = Shell(services);

        vm.ToEntry(PathScope.User, 1);
        Assert.Same(vm.UserEntries, vm.CurrentPage);
        Assert.Equal(new EntryOrigin(PathScope.User, 1), vm.UserEntries.Selected!.Entry.Origin);

        vm.ToLearn(LearnTopics.PathExt);
        Assert.Same(vm.Learn, vm.CurrentPage);
        Assert.True(vm.Learn.IsActive);
        Assert.False(vm.UserEntries.IsActive);

        vm.ToCommand("git");
        Assert.Same(vm.Shadowing, vm.CurrentPage);
        Assert.Equal("git", vm.Shadowing.Query);

        vm.ToEntries(PathScope.Machine);
        Assert.Same(vm.SystemEntries, vm.CurrentPage);
    }

    [Fact]
    public void Scan_on_launch_is_on_by_default_and_persists_when_turned_off()
    {
        using var store = new TempStore();
        using var services = new AppServices(store.Root);
        var settings = new SettingsViewModel(services, Sessions.NeverScans());

        Assert.True(settings.ScanOnLaunch);
        Assert.False(settings.CanExport);

        settings.ScanOnLaunch = false;
        Assert.False(services.Settings.Get().ScanOnLaunch);
    }

    [Fact]
    public void Export_writes_a_redacted_snapshot()
    {
        using var store = new TempStore();
        using var services = new AppServices(store.Root);
        var settings = new SettingsViewModel(services, Sessions.Showing(PosedMachines.Messy()));
        var path = Path.Combine(store.Root, "export.json");

        settings.Export(path);

        Assert.False(settings.ExportFailed, settings.ExportStatus);
        var written = PathSnapshotJson.Read(path);
        Assert.NotNull(written);
        Assert.True(written.Redacted);
        Assert.DoesNotContain(@"C:\Users\you", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }
}
