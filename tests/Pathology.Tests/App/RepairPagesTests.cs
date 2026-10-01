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

public class FixViewModelTests
{
    /// <summary>A session over the messy PC whose "scan" hands back the same snapshot: nothing real is scanned.</summary>
    static ScanSession Session()
    {
        var snapshot = PosedMachines.Messy();
        var session = new ScanSession((_, _) => snapshot);
        session.Show(ScanResult.Of(snapshot));
        return session;
    }

    static FixViewModel Page(out RecordingRepair repair, ScanSession? session = null)
    {
        repair = new RecordingRepair();
        return new FixViewModel(session ?? Session(), new RecordingNavigator(), repair);
    }

    [Fact]
    public void The_recommended_fixes_start_ticked_and_show_what_they_change()
    {
        var page = Page(out _);

        Assert.Equal(page.Fixes.Select(f => f.Fix.Recommended), page.Fixes.Select(f => f.IsSelected));
        Assert.False(page.Changes.IsEmpty);
        Assert.True(page.Plan!.HasChanges);
        Assert.Contains(page.Plan.Ratings, r => r.Changes);
        Assert.True(page.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void Nothing_ticked_and_nothing_edited_has_nothing_to_apply()
    {
        var page = Page(out _);
        foreach (var fix in page.Fixes) fix.IsSelected = false;

        Assert.True(page.Changes.IsEmpty);
        Assert.False(page.Plan!.HasChanges);
        Assert.False(page.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void A_choice_survives_a_rescan()
    {
        var session = Session();
        var page = Page(out _, session);
        var first = page.Fixes.First(f => f.IsSelected);
        first.IsSelected = false;

        session.Show(ScanResult.Of(PosedMachines.Messy()));

        Assert.False(page.Fixes.Single(f => f.Fix.Id == first.Fix.Id).IsSelected);
    }

    [Fact]
    public void Editor_buttons_shape_the_change_set()
    {
        var page = Page(out _);
        foreach (var fix in page.Fixes) fix.IsSelected = false;

        var machine = page.Sections[0];
        var tools = machine.Rows.Single(r => r.Entry.Text == @"C:\Tools");
        tools.MoveScopeCommand.Execute(null);
        page.NewEntryText = @"C:\Users\you\new\bin";
        page.AddCommand.Execute(null);

        var user = page.Changes.ValueFor(PathScope.User)!.After.Value!;
        Assert.StartsWith(@"C:\Tools;", user);
        Assert.EndsWith(@";C:\Users\you\new\bin", user.TrimEnd(';'));
        Assert.DoesNotContain(@"C:\Tools;", page.Changes.ValueFor(PathScope.Machine)!.After.Value!);
        Assert.True(page.HasEdits);

        page.ResetEditsCommand.Execute(null);
        Assert.True(page.Changes.IsEmpty);
    }

    [Fact]
    public void Removing_and_editing_an_entry_land_in_the_value()
    {
        var page = Page(out _);
        foreach (var fix in page.Fixes) fix.IsSelected = false;

        page.Sections[0].Rows.Single(r => r.Entry.Text == @"C:\OldApp\bin").RemoveCommand.Execute(null);
        var git = page.Sections[0].Rows.First(r => r.Entry.Text == @"C:\Program Files\Git\cmd");
        git.BeginEditCommand.Execute(null);
        git.EditText = @"C:\Program Files\Git\bin";
        git.SaveEditCommand.Execute(null);

        var machine = page.Changes.ValueFor(PathScope.Machine)!.After.Value!;
        Assert.DoesNotContain(@"C:\OldApp\bin", machine);
        Assert.Contains(@"C:\Program Files\Git\bin", machine);
        Assert.Contains(page.Plan!.Values.Single().Lines, l => l.Marker == "−" && l.Text == @"C:\OldApp\bin");
    }

    [Fact]
    public async Task Apply_asks_first_then_applies_exactly_what_is_shown_once()
    {
        var page = Page(out var repair);
        var shown = page.Changes;

        page.ApplyCommand.Execute(null);
        Assert.True(page.IsConfirming);
        Assert.Empty(repair.Applied);

        page.CancelApplyCommand.Execute(null);
        Assert.False(page.IsConfirming);
        Assert.Empty(repair.Applied);

        page.ApplyCommand.Execute(null);
        await page.ConfirmCommand.ExecuteAsync(null);

        var (changes, summary, fixes, undoOf) = Assert.Single(repair.Applied);
        Assert.Same(shown, changes);
        Assert.Null(undoOf);
        Assert.Equal(page.Fixes.Count(f => f.IsSelected), fixes.Count);
        Assert.EndsWith("fixes", summary);
        Assert.False(page.IsConfirming);
        Assert.False(page.ApplyFailed);
        Assert.StartsWith("Done.", page.ApplyStatus);
    }

    [Fact]
    public async Task A_declined_prompt_is_reported_and_not_called_a_success()
    {
        var page = Page(out var repair);
        repair.Outcome = ChangeOutcome.Cancelled;

        page.ApplyCommand.Execute(null);
        await page.ConfirmCommand.ExecuteAsync(null);

        Assert.True(page.ApplyFailed);
        Assert.Contains("nothing was changed", page.ApplyStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Arriving_from_an_entry_picks_it_out()
    {
        var page = Page(out _);
        page.Show(PathScope.Machine, 0);

        Assert.True(page.Sections.SelectMany(s => s.Rows).Single(r => r.IsHighlighted).Entry.Origin == new EntryOrigin(PathScope.Machine, 0));
    }

    [Fact]
    public void Folder_lock_downs_show_their_lines_and_who_applies_them()
    {
        var page = Page(out _);

        var tools = Assert.Single(page.Plan!.Acls, a => a.Path == @"C:\Tools");
        Assert.Equal("needs admin", tools.Badge);
        Assert.NotEmpty(tools.Lines);
        Assert.Contains("icacls", page.Plan.CommandsText);
        Assert.StartsWith("One UAC prompt", page.Plan.AdminLine);
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
