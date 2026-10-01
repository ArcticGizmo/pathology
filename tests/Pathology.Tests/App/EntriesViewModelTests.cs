using Pathology.App.Controls;
using Pathology.App.Rendering;
using Pathology.App.ViewModels;
using Pathology.Core.Model;

namespace Pathology.Tests.App;

public class EntriesViewModelTests
{
    static EntriesViewModel Page() => new(Sessions.Showing(PosedMachines.Messy()), new RecordingNavigator());

    static EntryRowViewModel Row(EntriesViewModel vm, PathScope scope, string expandedStart) =>
        vm.Sections.SelectMany(s => s.Rows).First(r => r.Entry.Scope == scope && r.Entry.Expanded.StartsWith(expandedStart, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Both_scopes_are_listed_in_search_order_without_the_trailing_separator()
    {
        var vm = Page();

        Assert.Equal(["MACHINE PATH", "USER PATH"], vm.Sections.Select(s => s.Heading));
        Assert.StartsWith("REG_EXPAND_SZ", vm.Sections[0].Summary);
        Assert.DoesNotContain(vm.Sections.SelectMany(s => s.Rows), r => r.Entry.Defects.HasFlag(HygieneDefects.TrailingSeparator));
        Assert.Equal(vm.Sections[0].Rows.Select(r => r.Entry.Index).Order(), vm.Sections[0].Rows.Select(r => r.Entry.Index));
    }

    [Fact]
    public void The_matrix_shows_who_can_add_files()
    {
        var vm = Page();

        var tools = Row(vm, PathScope.Machine, @"C:\Tools");
        Assert.True(tools.Matrix.Single(c => c.Perspective == Perspective.StandardUser).Writable);
        Assert.True(tools.HasFinding);

        var system32 = Row(vm, PathScope.Machine, @"C:\Windows\system32");
        Assert.False(system32.Matrix.Single(c => c.Perspective == Perspective.StandardUser).Writable);
        Assert.False(system32.Matrix.Single(c => c.Perspective == Perspective.CurrentUserUnelevated).Writable);

        // A folder that doesn't exist has no answer, not "no".
        var missing = Row(vm, PathScope.Machine, @"C:\OldApp");
        Assert.All(missing.Matrix, c => Assert.Null(c.Writable));
        Assert.Equal("missing", missing.Status);
    }

    [Fact]
    public void An_unexpanded_variable_says_so_rather_than_relative()
    {
        var row = Row(Page(), PathScope.Machine, "%JAVA_HOME%");
        Assert.Equal("%JAVA_HOME% isn't expanded", row.Status);
    }

    [Fact]
    public void Toggling_expanded_switches_every_row_to_the_stored_text()
    {
        var vm = Page();
        var row = Row(vm, PathScope.User, @"C:\Users\you\AppData\Local\Microsoft\WindowsApps");

        vm.ShowExpanded = false;

        Assert.Equal(@"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps", row.Text);
    }

    [Fact]
    public void Arriving_from_a_finding_selects_the_entry_and_details_who_can_write_it()
    {
        var vm = Page();

        vm.Select(PathScope.Machine, 0);

        Assert.Equal(@"C:\Tools", vm.Selected?.Entry.Expanded);
        var detail = vm.Detail!;
        Assert.True(detail.HasAccess);
        var standard = detail.Access.Single(a => a.Who == "A standard user");
        Assert.Contains("add files", standard.Can);
        Assert.Contains("Users", standard.Through);
        Assert.True(detail.HasFindings);
    }

    [Theory]
    [InlineData(@"""C:\Tools""", new[] { "\"", "C:\\Tools", "\"" })]
    [InlineData(@"  C:\Tools ", new[] { "··", "C:\\Tools", "·" })]
    [InlineData(@"C:\\Tools/bin", new[] { "C:", "\\\\", "Tools", "/", "bin" })]
    [InlineData(@"\\server\share", new[] { "\\\\server\\share" })]
    public void Defects_are_split_out_of_the_text(string text, string[] expected)
    {
        Assert.Equal(expected, DefectSegmenter.Split(text).Select(s => s.Text));
    }

    [Fact]
    public void Clean_text_is_one_plain_segment()
    {
        var segment = Assert.Single(DefectSegmenter.Split(@"C:\Windows\System32"));
        Assert.Equal(TextDefect.None, segment.Defect);
    }
}
