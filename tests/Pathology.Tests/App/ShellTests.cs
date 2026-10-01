using Pathology.App;
using Pathology.App.Rendering;
using Pathology.App.ViewModels;
using Pathology.Core.Detection;
using Pathology.Core.Model;

namespace Pathology.Tests.App;

public class ShellTests
{
    [Fact]
    public void The_nav_badges_count_high_problems_and_entries()
    {
        using var store = new TempStore();
        using var services = new AppServices(store.Root);
        var session = Sessions.Showing(PosedMachines.Messy());

        var vm = new MainWindowViewModel(services, session, new PosedRepair(), checkForUpdates: false);

        Assert.Equal(session.Current!.Health.Count(Severity.High), vm.Findings.NavCount);
        Assert.True(vm.Findings.NavCount > 0);
        Assert.Equal(session.Current.Snapshot.Entries.Count, vm.Entries.NavCount);
    }

    [Fact]
    public void Fix_and_History_sit_in_their_own_Repair_section()
    {
        using var store = new TempStore();
        using var services = new AppServices(store.Root);
        var vm = new MainWindowViewModel(services, Sessions.Showing(PosedMachines.Messy()), new PosedRepair(), checkForUpdates: false);

        var labels = vm.NavItems.Select(i => i is NavHeaderViewModel h ? "#" + h.Label : ((PageViewModel)i).Title).ToList();
        Assert.Equal(["#Diagnose", "Health", "Findings", "Entries", "Shadowing", "#Repair", "Fix", "History", "#Understand", "Learn"], labels);
        Assert.True(vm.Fix.NavCount > 0);

        vm.Entries.Select(PathScope.Machine, 0);
        vm.Entries.Detail!.ChangeInFixCommand.Execute(null);
        Assert.Same(vm.Fix, vm.CurrentPage);
        Assert.Contains(vm.Fix.Sections.SelectMany(s => s.Rows), r => r.IsHighlighted);
    }

    [Fact]
    public void Navigating_lands_on_the_page_with_the_thing_selected()
    {
        using var store = new TempStore();
        using var services = new AppServices(store.Root);
        var vm = new MainWindowViewModel(services, Sessions.Showing(PosedMachines.Messy()), new PosedRepair(), checkForUpdates: false);

        vm.ToEntry(PathScope.User, 1);
        Assert.Same(vm.Entries, vm.CurrentPage);
        Assert.Equal((PathScope.User, 1), (vm.Entries.Selected!.Entry.Scope, vm.Entries.Selected.Entry.Index));

        vm.ToLearn(LearnTopics.PathExt);
        Assert.Same(vm.Learn, vm.CurrentPage);
        Assert.True(vm.Learn.IsActive);
        Assert.False(vm.Entries.IsActive);

        vm.ToCommand("git");
        Assert.Same(vm.Shadowing, vm.CurrentPage);
        Assert.Equal("git", vm.Shadowing.Query);
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
