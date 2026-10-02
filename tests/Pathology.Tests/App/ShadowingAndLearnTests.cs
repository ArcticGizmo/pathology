using System.Reflection;
using Pathology.App.Rendering;
using Pathology.App.ViewModels;
using Pathology.Core.Detection;
using Pathology.Core.Learn;

namespace Pathology.Tests.App;

public class ShadowingViewModelTests
{
    static ShadowingViewModel Page() => new(Sessions.Showing(PosedMachines.Messy()), new RecordingNavigator());

    [Fact]
    public void A_lookup_shows_the_winner_then_what_it_hides()
    {
        var vm = Page();

        vm.Query = "python";

        var chain = vm.Resolution!.Chain;
        Assert.Equal("RUNS", chain[0].Label);
        Assert.Equal(@"C:\Python312\python.exe", chain[0].FullPath);
        Assert.Equal("any user could replace it", chain[0].Risk);
        Assert.Contains(chain.Skip(1), p => p.FullPath.EndsWith(@"WindowsApps\python.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_name_nothing_provides_says_so()
    {
        var vm = Page();

        vm.Query = "no-such-tool";

        Assert.False(vm.HasResolution);
        Assert.Equal("Nothing on PATH provides \"no-such-tool\".", vm.QueryNote);
    }

    [Fact]
    public void A_built_in_beaten_by_an_earlier_folder_is_listed_as_shadowed()
    {
        var vm = Page();

        var where = Assert.Single(vm.ShadowedBuiltins, b => b.Command == "where");
        Assert.Contains(@"C:\", where.By);
        Assert.True(vm.HasAtRisk);
        Assert.False(vm.BuiltinsSafe);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("worst")]
    public void With_System32_off_PATH_nothing_claims_the_built_ins_are_safe(string pose)
    {
        var snapshot = pose == "empty" ? PosedMachines.Empty() : PosedMachines.Worst();
        var vm = new ShadowingViewModel(Sessions.Showing(snapshot), new RecordingNavigator());

        Assert.True(vm.NoBuiltins);
        Assert.False(vm.BuiltinsSafe);
    }

    [Fact]
    public void A_stock_install_has_its_built_ins_safe()
    {
        var vm = new ShadowingViewModel(Sessions.Showing(PosedMachines.Clean()), new RecordingNavigator());

        Assert.True(vm.BuiltinsSafe);
    }

    [Fact]
    public void Competing_commands_put_a_writable_winner_first_unless_sorted_by_name()
    {
        var vm = Page();

        Assert.True(vm.Competing[0].WinnerWritable);

        vm.SortByRisk = false;
        Assert.Equal(vm.Competing.Select(c => c.Command).Order(StringComparer.OrdinalIgnoreCase), vm.Competing.Select(c => c.Command));
    }
}

public class LearnTests
{
    static IEnumerable<string> Topics() =>
        typeof(LearnTopics).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)!);

    [Fact]
    public void Every_topic_a_finding_can_link_to_has_an_article_with_a_body()
    {
        foreach (var topic in Topics())
        {
            var article = LearnLibrary.Find(topic);
            Assert.NotNull(article);
            Assert.True(article.Markdown.Length > 200, topic);
            Assert.False(string.IsNullOrWhiteSpace(article.Title));
        }
    }

    [Fact]
    public void Every_learn_link_on_a_real_diagnosis_resolves()
    {
        var diagnosis = Diagnoser.Diagnose(PosedMachines.Messy());

        Assert.All(diagnosis.Findings.Where(f => f.Learn is not null), f => Assert.NotNull(LearnLibrary.Find(f.Learn!)));
    }

    [Fact]
    public void Opening_a_topic_selects_its_article()
    {
        var vm = new LearnViewModel();

        vm.Open(LearnTopics.WhyNotSetx);

        Assert.Equal(LearnTopics.WhyNotSetx, vm.Selected?.Article.Id);
        Assert.Single(vm.Articles, a => a.IsSelected);
    }
}
