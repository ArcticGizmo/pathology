using Pathology.App.Rendering;
using Pathology.App.ViewModels;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Tests.Detection;

namespace Pathology.Tests.App;

public class HealthViewModelTests
{
    [Fact]
    public void Each_card_shows_its_rating_counts_and_what_it_takes_to_lower_it()
    {
        var vm = new HealthViewModel(Sessions.Showing(PosedMachines.Messy()), new RecordingNavigator());

        var security = vm.Cards.Single(c => c.Name == "Security");
        Assert.Equal("HIGH", security.RatingWord);
        Assert.StartsWith("Fix ", security.HintLine);
        Assert.EndsWith("to bring it to Medium", security.HintLine);
        Assert.InRange(security.Problems.Count, 1, 3);

        var hygiene = vm.Cards.Single(c => c.Name == "Hygiene");
        Assert.Equal("LOW", hygiene.RatingWord);
        Assert.EndsWith("to make it clean", hygiene.HintLine);
        Assert.False(vm.IsClean);
    }

    [Fact]
    public void A_stock_install_is_clean_everywhere_with_nothing_to_fix()
    {
        var vm = new HealthViewModel(Sessions.Showing(PosedMachines.Clean()), new RecordingNavigator());

        Assert.True(vm.IsClean);
        Assert.All(vm.Cards, c =>
        {
            Assert.Equal("CLEAN", c.RatingWord);
            Assert.Equal("Nothing to fix", c.CountsLine);
            Assert.Empty(c.Problems);
        });
        // The WindowsApps folder every user gets is the baseline, noted but not a problem.
        Assert.Equal("Only the Windows baseline", vm.UacHeadline);
    }

    [Fact]
    public void Uac_exposure_does_not_apply_to_a_standard_user()
    {
        var machine = new TestMachine(@"%SystemRoot%\system32", @"C:\Users\you\bin") { Admin = false }
            .Folder(@"C:\Users\you\bin", f => f.WritableByYou());

        var vm = new HealthViewModel(Sessions.Showing(machine.Snapshot()), new RecordingNavigator());

        Assert.Equal("Not applicable", vm.UacHeadline);
        Assert.Empty(vm.UacFolders);
    }

    [Fact]
    public void Clicking_a_problem_or_a_card_opens_findings_for_it()
    {
        var nav = new RecordingNavigator();
        var vm = new HealthViewModel(Sessions.Showing(PosedMachines.Messy()), nav);
        var card = vm.Cards.Single(c => c.Name == "Correctness");

        card.Problems[0].OpenCommand.Execute(null);
        card.OpenCommand.Execute(null);

        Assert.Equal(new FindingsQuery(RootCause: card.Problems[0].Group.RootCause), nav.Calls[0]);
        Assert.Equal(new FindingsQuery(Category: FindingCategory.Correctness), nav.Calls[1]);
    }

    [Fact]
    public void Headroom_bars_turn_medium_at_80_percent_of_a_limit()
    {
        var bar = new HeadroomBarViewModel("x", 1700, 2047);
        Assert.Equal(1700 / 2047.0, bar.Fraction, 6);
        Assert.Equal("1,700 of 2,047 (83%)", bar.Text);
    }

    [Fact]
    public void Nothing_scanned_shows_no_cards()
    {
        var vm = new HealthViewModel(Sessions.NeverScans(), new RecordingNavigator());

        Assert.Empty(vm.Cards);
        Assert.True(vm.IsIdle);
        Assert.Equal("Scan", vm.RescanLabel);
    }
}
