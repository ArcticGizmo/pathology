using Pathology.App.Rendering;
using Pathology.App.Repair;
using Pathology.App.Scanning;
using Pathology.App.ViewModels;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Tests.App;

/// <summary>
/// A repair service that designs and projects like the renderer's, and records what it's asked to apply instead
/// of writing anything.
/// </summary>
internal sealed class RecordingRepair : IRepairService
{
    readonly PosedRepair _posed = new();

    public List<(ChangeSet Changes, string Summary, IReadOnlyList<string> Fixes, Guid? UndoOf)> Applied { get; } = [];
    public List<ChangeRecord> Records { get; } = [];
    public ChangeOutcome Outcome { get; set; } = ChangeOutcome.Applied;

    public IAclDesigner Designer => _posed.Designer;
    public PlanOutcome Project(Diagnosis before, ChangeSet changes) => _posed.Project(before, changes);
    public IReadOnlyList<ChangeRecord> History() => Records.OrderByDescending(r => r.StartedAt).ToList();

    public ChangeSet Undo(ChangeRecord record) => new()
    {
        Values = record.Changes.Values.Select(v => new ValueChange { Before = v.After, After = v.Before }).ToList(),
    };

    public ChangeRecord Apply(ChangeSet changes, string summary, IReadOnlyList<string> fixes, Guid? undoOf = null)
    {
        Applied.Add((changes, summary, fixes, undoOf));
        var record = new ChangeRecord
        {
            Id = changes.Id, StartedAt = DateTimeOffset.UtcNow, Summary = summary, Fixes = fixes, Changes = changes, UndoOf = undoOf,
            Outcome = Outcome,
            Steps = changes.Values.Select(v => new StepResult(StepResult.TargetOf(v.Scope),
                Outcome == ChangeOutcome.Applied ? StepStatus.Applied : StepStatus.Skipped)).ToList(),
        };
        Records.Add(record);
        return record;
    }
}

public class ReviewViewModelTests
{
    /// <summary>A session over the messy PC whose "scan" hands back the same snapshot: nothing real is scanned.</summary>
    static ScanSession Session()
    {
        var snapshot = PosedMachines.Messy();
        var session = new ScanSession((_, _) => snapshot);
        session.Show(ScanResult.Of(snapshot));
        return session;
    }

    static ReviewViewModel Page(out EntryPages pages, out RecordingNavigator nav)
    {
        pages = new EntryPages(Session());
        nav = pages.Navigator;
        return new ReviewViewModel(pages.Pending, nav);
    }

    [Fact]
    public void Nothing_staged_has_nothing_to_apply()
    {
        var page = Page(out _, out _);

        Assert.Equal("Nothing is staged.", page.Summary);
        Assert.False(page.Plan!.HasChanges);
        Assert.False(page.ApplyCommand.CanExecute(null));
        Assert.False(page.DiscardCommand.CanExecute(null));
    }

    [Fact]
    public void The_recommended_fixes_staged_show_what_they_change()
    {
        var page = Page(out var pages, out _);

        pages.Pending.StageRecommended();

        Assert.Equal(pages.Pending.Fixes.Count(f => f.Fix.Recommended), page.Staged.Count);
        Assert.True(page.Plan!.HasChanges);
        Assert.Contains(page.Plan.Ratings, r => r.Changes);
        Assert.True(page.ApplyCommand.CanExecute(null));
        Assert.False(page.HasEdits);
    }

    [Fact]
    public void Unstaging_a_fix_here_unstages_it_on_its_entry()
    {
        var page = Page(out var pages, out _);
        pages.Pending.StageRecommended();
        var tools = page.Staged.Single(f => f.Title == @"Lock down C:\Tools");

        tools.ToggleCommand.Execute(null);

        Assert.DoesNotContain(page.Staged, f => f.Title == @"Lock down C:\Tools");
        Assert.DoesNotContain(EntryPages.Line(pages.System, @"C:\Tools").Fixes, f => f.IsStaged);
    }

    [Fact]
    public void Your_own_edits_are_counted_with_the_fixes()
    {
        var page = Page(out var pages, out _);
        EntryPages.Line(pages.System, @"C:\OldApp\bin").DeleteCommand.Execute(null);
        EntryPages.Line(pages.System, @"C:\Tools").Fixes.First().ToggleCommand.Execute(null);

        Assert.Equal("1 fix and 1 edit staged.", page.Summary);
        Assert.StartsWith("Plus 1 edit", page.EditsLine);
    }

    [Fact]
    public async Task Apply_asks_first_then_applies_exactly_what_is_shown_once()
    {
        var page = Page(out var pages, out _);
        pages.Pending.StageRecommended();
        var shown = pages.Pending.Changes;
        var staged = page.Staged.Count;

        page.ApplyCommand.Execute(null);
        Assert.True(page.IsConfirming);
        Assert.Empty(pages.Repair.Applied);

        page.CancelApplyCommand.Execute(null);
        Assert.False(page.IsConfirming);
        Assert.Empty(pages.Repair.Applied);

        page.ApplyCommand.Execute(null);
        await page.ConfirmCommand.ExecuteAsync(null);

        var (changes, summary, fixes, undoOf) = Assert.Single(pages.Repair.Applied);
        Assert.Same(shown, changes);
        Assert.Null(undoOf);
        Assert.Equal(staged, fixes.Count);
        Assert.EndsWith("fixes", summary);
        Assert.False(page.IsConfirming);
        Assert.False(page.ApplyFailed);
        Assert.StartsWith("Done.", page.ApplyStatus);
        // What was staged is written: the re-scan starts again with nothing staged.
        Assert.False(pages.Pending.HasStaged);
    }

    [Fact]
    public async Task A_declined_prompt_is_reported_and_not_called_a_success()
    {
        var page = Page(out var pages, out _);
        pages.Pending.StageRecommended();
        pages.Repair.Outcome = ChangeOutcome.Cancelled;

        page.ApplyCommand.Execute(null);
        await page.ConfirmCommand.ExecuteAsync(null);

        Assert.True(page.ApplyFailed);
        Assert.Contains("nothing was changed", page.ApplyStatus, StringComparison.OrdinalIgnoreCase);
        // Nothing was written, so it's all still staged.
        Assert.True(pages.Pending.HasStaged);
    }

    [Fact]
    public void Folder_lock_downs_show_their_lines_and_who_applies_them()
    {
        var page = Page(out var pages, out _);
        pages.Pending.StageRecommended();

        var tools = Assert.Single(page.Plan!.Acls, a => a.Path == @"C:\Tools");
        Assert.Equal("needs admin", tools.Badge);
        Assert.NotEmpty(tools.Lines);
        Assert.Contains("icacls", page.Plan.CommandsText);
        Assert.StartsWith("One UAC prompt", page.Plan.AdminLine);
    }

    [Fact]
    public void Search_the_Windows_folders_first_says_which_commands_change()
    {
        var page = Page(out var pages, out _);
        pages.Pending.Fixes.Single(f => f.Fix.Id == RemediationPlanner.WindowsFirstId).IsStaged = true;

        Assert.True(page.Plan!.HasCommands);
        Assert.Contains(page.Plan.Values.Single().Lines, l => l.Marker == "↕");
    }

    [Fact]
    public void Back_goes_to_the_PATH_the_change_is_in()
    {
        var page = Page(out var pages, out var nav);
        EntryPages.Line(pages.User, @"C:\Users\you\.dotnet\tools").DeleteCommand.Execute(null);

        page.BackCommand.Execute(null);

        Assert.Equal(PathScope.User, Assert.Single(nav.Calls));
    }
}

public class HistoryViewModelTests
{
    [Fact]
    public void Records_are_listed_newest_first_with_their_outcome()
    {
        var repair = new RecordingRepair();
        repair.Records.AddRange(PosedRepair.PosedHistory());
        var page = new HistoryViewModel(repair, Sessions.NeverScans());

        Assert.Equal(4, page.Rows.Count);
        Assert.Equal("APPLIED", page.Rows[0].OutcomeWord);
        Assert.Equal("CANCELLED", page.Rows[1].OutcomeWord);
        Assert.Same(page.Rows[0], page.Selected);
        Assert.True(page.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void Nothing_undone_or_never_applied_cannot_be_undone()
    {
        var repair = new RecordingRepair();
        repair.Records.AddRange(PosedRepair.PosedHistory());
        var page = new HistoryViewModel(repair, Sessions.NeverScans());

        page.Selected = page.Rows.Single(r => r.OutcomeWord == "CANCELLED");
        Assert.False(page.UndoCommand.CanExecute(null));
        page.Selected = page.Rows.Single(r => r.OutcomeWord == "UNDONE");
        Assert.False(page.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Undo_confirms_then_applies_the_reverse_as_an_undo_of_the_record()
    {
        var repair = new RecordingRepair();
        repair.Records.AddRange(PosedRepair.PosedHistory());
        var snapshot = PosedMachines.Messy();
        var session = new ScanSession((_, _) => snapshot);
        var page = new HistoryViewModel(repair, session);
        var record = page.Selected!.Record;

        page.UndoCommand.Execute(null);
        Assert.True(page.IsConfirming);
        await page.ConfirmUndoCommand.ExecuteAsync(null);

        var (changes, summary, _, undoOf) = Assert.Single(repair.Applied);
        Assert.Equal(record.Id, undoOf);
        Assert.Equal($"Undo of {record.Summary}", summary);
        Assert.Equal(record.Changes.Values[0].Before, changes.Values[0].After);
        Assert.False(page.StatusFailed);
        Assert.Equal(5, page.Rows.Count);
    }

    [Fact]
    public void An_empty_history_says_so()
    {
        var page = new HistoryViewModel(new RecordingRepair(), Sessions.NeverScans());

        Assert.True(page.IsEmpty);
        Assert.False(page.HasSelection);
    }
}
