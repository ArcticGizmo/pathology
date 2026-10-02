using Pathology.App.Controls;
using Pathology.App.ViewModels;
using Pathology.Core.Model;
using Pathology.Core.Remediation;
using static Pathology.Tests.App.EntryPages;

namespace Pathology.Tests.App;

public class EntriesViewModelTests
{
    [Fact]
    public void Each_page_lists_its_own_PATH_in_search_order()
    {
        var pages = new EntryPages();

        Assert.Equal(["System", "User"], new[] { pages.System.Title, pages.User.Title });
        Assert.True(pages.System.IsNested);
        Assert.StartsWith("REG_EXPAND_SZ", pages.System.Summary);
        var scanned = pages.Pending.Result!.Snapshot;
        Assert.All(pages.System.Rows, r => Assert.Equal(PathScope.Machine, r.Scanned!.Scope));
        Assert.Equal(pages.System.Rows.Select(r => r.Scanned!.Index).Order(), pages.System.Rows.Select(r => r.Scanned!.Index));
        // The empty slot Windows' trailing ';' leaves isn't an entry.
        Assert.DoesNotContain(pages.User.Rows, r => r.Scanned!.Defects.HasFlag(HygieneDefects.TrailingSeparator));
        Assert.Equal(scanned.EntriesIn(PathScope.Machine).Count(e => !e.Defects.HasFlag(HygieneDefects.TrailingSeparator)), pages.System.Rows.Count);
    }

    [Fact]
    public void Nothing_is_staged_to_begin_with()
    {
        var pages = new EntryPages();

        Assert.False(pages.Pending.HasStaged);
        Assert.True(pages.Pending.Changes.IsEmpty);
        Assert.DoesNotContain(pages.System.Rows, r => r.HasChange || r.IsGhost);
        Assert.Equal("Nothing staged.", pages.System.PendingSummary);
        Assert.False(pages.System.ReviewCommand.CanExecute(null));
        Assert.True(pages.System.CanStageRecommended);
    }

    [Fact]
    public void A_line_says_what_is_there_and_what_it_expands_to()
    {
        var pages = new EntryPages();

        Assert.Equal("missing", Line(pages.System, @"C:\OldApp\bin").Status);
        Assert.Equal("%JAVA_HOME% isn't expanded", Line(pages.System, @"%JAVA_HOME%\bin").Status);

        var expands = Line(pages.User, @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps");
        Assert.Equal(@"C:\Users\you\AppData\Local\Microsoft\WindowsApps", expands.Expanded);
        Assert.False(Line(pages.System, @"C:\Tools").HasExpanded);
    }

    [Fact]
    public void Picking_an_entry_shows_its_problems_the_fix_for_each_and_who_can_write_it()
    {
        var pages = new EntryPages();

        Assert.True(pages.System.Select(PathScope.Machine, 0));
        var panel = pages.System.Panel!;

        Assert.Equal("System PATH #1", panel.Heading);
        var writable = panel.Issues.First(i => i.Group.Members.Any(f => f.Rule == "SEC-01"));
        Assert.Contains(writable.Fixes, f => f.Title == @"Lock down C:\Tools");
        Assert.True(writable.HasLearn);
        Assert.All(panel.Issues, i => Assert.NotEqual(Pathology.Core.Detection.Severity.Info, i.Group.Severity));

        var standard = panel.Access.Single(a => a.Who == "A standard user");
        Assert.Contains("add files", standard.Can);
        Assert.Contains("Users", standard.Through);
    }

    [Fact]
    public void A_problem_a_move_settles_offers_the_move()
    {
        // A folder in your profile that's in the system PATH: writable by you, and in the wrong PATH. Moving it to
        // your user PATH settles both, though the move is only meant for the second.
        var pages = new EntryPages();
        var code = Line(pages.System, @"C:\Users\you\AppData\Local\Programs\Microsoft VS Code\bin");
        pages.System.Select(code);

        var writable = pages.System.Panel!.Issues.Single(i => i.Group.Members.Any(f => f.Rule == "SEC-01"));
        var move = Assert.Single(writable.Fixes);
        Assert.Contains(move.Fix.Edits, e => e is MoveToUser);
        Assert.False(writable.HasAdvice);
    }

    [Fact]
    public void A_fix_staged_on_one_line_is_staged_on_every_line_it_changes()
    {
        var pages = new EntryPages();
        var python = pages.System.Rows.Where(r => r.Text.StartsWith(@"C:\Python312", StringComparison.Ordinal)).ToList();
        var shared = python[0].Fixes.Single(f => f.Title.Contains("inherit", StringComparison.Ordinal));
        Assert.Equal(2, shared.Lines);

        shared.ToggleCommand.Execute(null);

        Assert.True(pages.Pending.HasStaged);
        Assert.Equal("1 fix staged", pages.System.PendingSummary);
        var after = pages.System.Rows.Where(r => r.Text.StartsWith(@"C:\Python312", StringComparison.Ordinal)).ToList();
        Assert.All(after, r => Assert.StartsWith("fix staged", r.Change));
        Assert.True(pages.System.ReviewCommand.CanExecute(null));
        Assert.True(pages.System.HasRatingChanges || pages.Pending.Plan!.HasChanges);
    }

    [Fact]
    public void Whole_PATH_fixes_sit_on_the_heading_and_every_fix_shows_somewhere()
    {
        var pages = new EntryPages();

        Assert.Contains(pages.System.ValueFixes, f => f.Fix.Id == RemediationPlanner.WindowsFirstId);
        Assert.DoesNotContain(pages.System.Rows.SelectMany(r => r.Fixes), f => f.Fix.Id == RemediationPlanner.WindowsFirstId);

        var shown = new[] { pages.System, pages.User }
            .SelectMany(p => p.Rows.SelectMany(r => r.Fixes).Concat(p.ValueFixes))
            .ToHashSet();
        Assert.All(pages.Pending.Fixes, f => Assert.Contains(f, shown));
    }

    [Fact]
    public void Deleting_a_line_strikes_it_through_where_it_was_until_it_is_put_back()
    {
        var pages = new EntryPages();
        var old = Line(pages.System, @"C:\OldApp\bin");
        pages.System.Select(old);

        old.DeleteCommand.Execute(null);

        var gone = pages.System.Rows.Single(r => r.IsGhost);
        Assert.Equal("removed", gone.Change);
        Assert.Same(gone, pages.System.Selected);
        Assert.True(gone.CanPutBack);
        Assert.False(gone.DeleteCommand.CanExecute(null));
        Assert.DoesNotContain(@"C:\OldApp\bin", pages.Pending.Changes.ValueFor(PathScope.Machine)!.After.Value!);
        Assert.Equal("1 edit staged", pages.System.PendingSummary);

        gone.PutBackCommand.Execute(null);

        Assert.True(pages.Pending.Changes.IsEmpty);
        Assert.DoesNotContain(pages.System.Rows, r => r.IsGhost);
        Assert.False(pages.System.Selected!.IsGhost);
    }

    [Fact]
    public void A_line_a_fix_removes_stays_struck_through_with_the_fix_to_unstage()
    {
        var pages = new EntryPages();
        var old = Line(pages.System, @"C:\OldApp\bin");
        old.Fixes.Single().ToggleCommand.Execute(null);

        var gone = pages.System.Rows.Single(r => r.IsGhost);
        Assert.Equal("removed", gone.Change);
        Assert.False(gone.CanPutBack);
        Assert.True(gone.Fixes.Single().IsStaged);

        gone.Fixes.Single().ToggleCommand.Execute(null);
        Assert.DoesNotContain(pages.System.Rows, r => r.IsGhost);
    }

    [Fact]
    public void Moving_to_your_user_PATH_shows_on_both_pages_and_writes_the_user_PATH_first()
    {
        var pages = new EntryPages();
        var tools = Line(pages.System, @"C:\Tools");
        Assert.True(tools.MoveScopeCommand.CanExecute(null));
        Assert.Equal("Move to your user PATH", tools.MoveLabel);

        tools.MoveScopeCommand.Execute(null);

        var gone = pages.System.Rows.Single(r => r.IsGhost);
        Assert.Equal("moves to the user PATH", gone.Change);
        Assert.Equal("Staged: moves to the user PATH", gone.StagedLine);
        Assert.True(gone.CanPutBack);
        var arrived = Line(pages.User, @"C:\Tools");
        Assert.Equal(0, arrived.Position);
        Assert.Equal("moved here from the system PATH", arrived.Change);
        Assert.NotNull(pages.Pending.Changes.UserFirst());
        Assert.Contains("written first", pages.Pending.Plan!.AdminLine);
    }

    [Fact]
    public void Moving_an_entry_your_user_PATH_already_has_removes_the_system_copy()
    {
        var pages = new EntryPages();
        // Give the user PATH its own copy (spelled differently), then move the system one.
        var dotnet = Line(pages.User, @"C:\Users\you\.dotnet\tools");
        dotnet.BeginEditCommand.Execute(null);
        dotnet.EditText = @"c:\tools\";
        dotnet.SaveEditCommand.Execute(null);

        Line(pages.System, @"C:\Tools").MoveScopeCommand.Execute(null);

        Assert.Equal("removed", pages.System.Rows.Single(r => r.IsGhost).Change);
        Assert.Single(pages.User.Rows, r => r.Text == @"c:\tools\");
        Assert.DoesNotContain(pages.User.Rows, r => r.Text == @"C:\Tools");
    }

    [Fact]
    public void Moving_one_line_up_marks_only_that_line_as_moved()
    {
        var pages = new EntryPages();
        var position = Line(pages.System, @"C:\Program Files\nodejs\").Position;

        // Two places up, past both Python folders: it's the one that moved, not the two it passed.
        Line(pages.System, @"C:\Program Files\nodejs\").MoveUpCommand.Execute(null);
        Line(pages.System, @"C:\Program Files\nodejs\").MoveUpCommand.Execute(null);

        var moved = pages.System.Rows.Where(r => r.Change.StartsWith("moved from", StringComparison.Ordinal)).ToList();
        Assert.Equal(@"C:\Program Files\nodejs\", Assert.Single(moved).Text);
        Assert.Equal(position - 2, Line(pages.System, @"C:\Program Files\nodejs\").Position);
        Assert.True(Line(pages.System, @"C:\Program Files\nodejs\").CanRevert);
    }

    [Fact]
    public void Editing_and_deleting_land_in_the_value_and_the_review()
    {
        var pages = new EntryPages();
        var git = pages.System.Rows.First(r => r.Text == @"C:\Program Files\Git\cmd");

        git.BeginEditCommand.Execute(null);
        Assert.True(git.IsEditing);
        git.EditText = @"C:\Program Files\Git\bin";
        git.SaveEditCommand.Execute(null);
        Line(pages.System, @"C:\OldApp\bin").DeleteCommand.Execute(null);

        var machine = pages.Pending.Changes.ValueFor(PathScope.Machine)!.After.Value!;
        Assert.Contains(@"C:\Program Files\Git\bin", machine);
        Assert.DoesNotContain(@"C:\OldApp\bin", machine);
        Assert.Equal(@"was C:\Program Files\Git\cmd", Line(pages.System, @"C:\Program Files\Git\bin").Change);
        Assert.Contains(pages.Pending.Plan!.Values.Single().Lines, l => l.Marker == "−" && l.Text == @"C:\OldApp\bin");
    }

    [Fact]
    public void Staging_the_recommended_fixes_then_discarding_leaves_nothing_staged()
    {
        var pages = new EntryPages();

        pages.System.StageRecommendedCommand.Execute(null);
        Assert.Equal(pages.Pending.Fixes.Count(f => f.Fix.Recommended), pages.Pending.Staged.Count);
        Assert.False(pages.System.CanStageRecommended);
        Assert.True(pages.User.HasStaged);

        pages.User.DiscardCommand.Execute(null);
        Assert.False(pages.Pending.HasStaged);
        Assert.True(pages.Pending.Changes.IsEmpty);
    }

    [Fact]
    public void A_pick_survives_a_rescan_but_hand_edits_do_not()
    {
        var pages = new EntryPages(new Pathology.App.Scanning.ScanSession((_, _) => Pathology.App.Rendering.PosedMachines.Messy()));
        pages.Session.Show(Pathology.App.Scanning.ScanResult.Of(Pathology.App.Rendering.PosedMachines.Messy()));
        var fix = Line(pages.System, @"C:\Tools").Fixes.First();
        fix.ToggleCommand.Execute(null);
        Line(pages.System, @"C:\OldApp\bin").DeleteCommand.Execute(null);

        pages.Session.Show(Pathology.App.Scanning.ScanResult.Of(Pathology.App.Rendering.PosedMachines.Messy()));

        Assert.True(pages.Pending.Fixes.Single(f => f.Fix.Id == fix.Fix.Id).IsStaged);
        Assert.Equal(0, pages.Pending.EditCount);
    }

    [Fact]
    public void The_nav_badge_counts_entries_with_a_problem()
    {
        var pages = new EntryPages();
        var result = pages.Pending.Result!;

        Assert.Equal(result.Snapshot.EntriesIn(PathScope.Machine).Count(e => result.GroupsFor(e).Any(g => g.Severity > Pathology.Core.Detection.Severity.Info)),
            pages.System.NavCount);
        Assert.True(pages.System.NavCount > 0);
    }

    [Fact]
    public void Review_opens_from_the_pending_bar()
    {
        var pages = new EntryPages();
        Line(pages.System, @"C:\OldApp\bin").DeleteCommand.Execute(null);

        pages.System.ReviewCommand.Execute(null);

        Assert.Equal("review", Assert.Single(pages.Navigator.Calls));
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
