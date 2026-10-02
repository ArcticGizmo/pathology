using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Theming;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Remediation;
using static Pathology.App.Theme;

namespace Pathology.App.ViewModels;

/// <summary>
/// The Review page: what you staged on the System and User pages, what applying it would change, and Apply. It's a
/// dry run until Apply is clicked and confirmed. Reached from the pending bar, not the nav.
/// </summary>
public sealed partial class ReviewViewModel : PageViewModel
{
    readonly INavigator _navigator;
    readonly Action? _applied;

    /// <param name="applied">Called after an apply wrote something (History refreshes).</param>
    public ReviewViewModel(PendingChanges pending, INavigator navigator, Action? applied = null)
    {
        Pending = pending;
        _navigator = navigator;
        _applied = applied;
        pending.Changed += (_, _) => Refresh();
    }

    public override string Title => "Review";

    public PendingChanges Pending { get; }

    public PlanViewModel? Plan => Pending.Plan;
    public IReadOnlyList<FixRowViewModel> Staged => Pending.Staged;
    public bool HasStagedFixes => Staged.Count > 0;
    public string Summary => Pending.HasStaged ? Pending.Summary + "." : "Nothing is staged.";

    /// <summary>"Plus 2 edits of your own: moves, reorders, text and deletions."</summary>
    public string EditsLine => Pending.EditCount == 0 ? ""
        : $"{(HasStagedFixes ? "Plus " : "")}{PendingChanges.Words(Pending.EditCount, "edit", "edits")} of your own: moves, reorders, text and deletions. They're shown on the System and User pages.";
    public bool HasEdits => Pending.EditCount > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(ConfirmCommand))]
    private bool _isConfirming;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(ConfirmCommand))]
    private bool _isApplying;

    /// <summary>How the last apply went, or "" before one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplyStatus))]
    private string _applyStatus = "";

    [ObservableProperty] private bool _applyFailed;
    [ObservableProperty] private IReadOnlyList<StepRowViewModel> _applySteps = [];

    public bool HasApplyStatus => ApplyStatus.Length > 0;

    void Refresh()
    {
        if (!IsApplying) IsConfirming = false;
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(Staged));
        OnPropertyChanged(nameof(HasStagedFixes));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(EditsLine));
        OnPropertyChanged(nameof(HasEdits));
        ApplyCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Back to the page the change is on.</summary>
    [RelayCommand]
    private void Back() => _navigator.ToEntries(Pending.Changes.Values.Select(v => v.Scope).DefaultIfEmpty(PathScope.Machine).First());

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard() => Pending.Discard();

    bool CanDiscard() => Pending.HasStaged && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply() => IsConfirming = true;

    bool CanApply() => !IsApplying && !IsConfirming && Plan is { HasChanges: true, HasProblems: false };

    [RelayCommand]
    private void CancelApply() => IsConfirming = false;

    /// <summary>Apply what's shown: the one path from the staged changes to the writers.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task Confirm()
    {
        var changes = Pending.Changes;
        var fixes = Pending.Staged.Select(f => f.Fix.Title).ToList();
        var summary = SummaryOf(fixes.Count, Pending.EditCount);
        IsApplying = true;
        try
        {
            var record = await AppServices.RunAsync(() => Pending.Repair.Apply(changes, summary, fixes));
            Report(record);
            if (record.Applied.Any())
            {
                _applied?.Invoke();
                // What was staged is written (or partly): start again from what the re-scan finds.
                Pending.Discard();
                await Pending.Session.ScanAsync();
            }
        }
        catch (Exception ex)
        {
            ApplyFailed = true;
            ApplyStatus = $"Nothing was applied: {ex.Message}";
            ApplySteps = [];
        }
        finally
        {
            IsApplying = false;
            IsConfirming = false;
        }
    }

    bool CanConfirm() => IsConfirming && !IsApplying;

    void Report(ChangeRecord record)
    {
        ApplyFailed = record.Outcome is not ChangeOutcome.Applied;
        ApplyStatus = Outcomes.Describe(record);
        ApplySteps = record.Steps.Select(s => new StepRowViewModel(s)).ToList();
    }

    /// <summary>Show the outcome of an apply without running one (the renderer's pose).</summary>
    internal void PoseOutcome(ChangeRecord record) => Report(record);

    static string SummaryOf(int fixes, int edits) =>
        (fixes, edits) switch
        {
            (0, _) => PendingChanges.Words(edits, "edit", "edits"),
            (_, 0) => PendingChanges.Words(fixes, "fix", "fixes"),
            _ => $"{PendingChanges.Words(fixes, "fix", "fixes")} and {PendingChanges.Words(edits, "edit", "edits")}",
        };
}

/// <summary>How an entry's text is shown in a diff or a status: "(empty)" for nothing, and edge spaces as <c>·</c>.</summary>
internal static class EntryWords
{
    public static string Shown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(empty)";
        var lead = text.Length - text.TrimStart().Length;
        var trail = text.Length - text.TrimEnd().Length;
        return new string('·', lead) + text.Trim() + new string('·', trail);
    }

    public static string ScopeName(PathScope scope) => scope == PathScope.Machine ? "system" : "user";
}

/// <summary>What applying would change, and what that does.</summary>
public sealed class PlanViewModel
{
    const int MaxCommands = 40;

    public PlanViewModel(ChangeSet changes, PlanOutcome? outcome, PathDraft draft, PathDraft baseline, Scanning.ScanResult result)
    {
        HasChanges = !changes.IsEmpty;
        Problems = changes.Problems();

        var after = outcome?.After.Health ?? result.Health;
        Ratings = result.Health.Categories
            .Select(c => new RatingChangeViewModel(c.Category.ToString(), c.Rating, after[c.Category].Rating))
            .ToList();

        Values = changes.Values.Select(v => new ValueDiffViewModel(v, draft, baseline)).ToList();
        Acls = changes.Acls.Select(a => new AclChangeViewModel(a)).ToList();
        CommandsText = string.Join(Environment.NewLine, changes.Acls.Where(a => a.Writes).SelectMany(a => a.Commands));

        var commands = outcome?.Commands ?? [];
        Commands = commands.Take(MaxCommands).Select(c => new CommandChangeViewModel(c)).ToList();
        MoreCommands = commands.Count > MaxCommands ? $"and {commands.Count - MaxCommands} more" : "";

        var resolved = outcome?.Resolved.Count(g => g.Severity > Severity.Info) ?? 0;
        ResolvedLine = resolved == 0 ? "" : $"Fixes {PendingChanges.Words(resolved, "problem", "problems")}.";
        Introduced = (outcome?.Introduced ?? []).Where(g => g.Severity > Severity.Info)
            .Select(g => new IntroducedViewModel(g.Primary.Title, Severities.BrushFor(g.Severity)))
            .ToList();

        var admin = new List<string>();
        if (changes.ValueFor(PathScope.Machine) is not null) admin.Add("the system PATH");
        var adminFolders = changes.Acls.Count(a => a.Writes && a.NeedsAdmin);
        if (adminFolders > 0) admin.Add(PendingChanges.Words(adminFolders, "folder", "folders"));
        AdminLine = !HasChanges ? ""
            : admin.Count == 0 ? "No UAC prompt: everything here is yours to change."
            : $"One UAC prompt, for {string.Join(" and ", admin)}."
              + (changes.UserFirst() is null ? ""
                  : " Your user PATH is written first, before the prompt, so a moved entry is never missing from both; if you decline, it's put back.");
    }

    public bool HasChanges { get; }
    public IReadOnlyList<string> Problems { get; }
    public bool HasProblems => Problems.Count > 0;

    public IReadOnlyList<RatingChangeViewModel> Ratings { get; }

    /// <summary>Just the categories whose rating moves, for the pending bar.</summary>
    public IReadOnlyList<RatingChangeViewModel> RatingChanges => Ratings.Where(r => r.Changes).ToList();
    public bool HasRatingChanges => Ratings.Any(r => r.Changes);

    public IReadOnlyList<ValueDiffViewModel> Values { get; }
    public bool HasValues => Values.Count > 0;
    public IReadOnlyList<AclChangeViewModel> Acls { get; }
    public bool HasAcls => Acls.Count > 0;
    public string CommandsText { get; }
    public bool HasCommandsText => CommandsText.Length > 0;
    public IReadOnlyList<CommandChangeViewModel> Commands { get; }
    public bool HasCommands => Commands.Count > 0;
    public string MoreCommands { get; }
    public bool HasMoreCommands => MoreCommands.Length > 0;
    public string ResolvedLine { get; }
    public bool HasResolved => ResolvedLine.Length > 0;
    public IReadOnlyList<IntroducedViewModel> Introduced { get; }
    public bool HasIntroduced => Introduced.Count > 0;
    public string AdminLine { get; }
}

public sealed record IntroducedViewModel(string Title, IBrush Brush);

/// <summary>One category: its rating now and after.</summary>
public sealed class RatingChangeViewModel(string name, Severity? before, Severity? after)
{
    public string Name { get; } = name;
    public string Before { get; } = Severities.RatingWord(before);
    public IBrush BeforeBrush { get; } = Severities.BrushFor(before);
    public string After { get; } = Severities.RatingWord(after);
    public IBrush AfterBrush { get; } = Severities.BrushFor(after);
    public bool Changes { get; } = before != after;
}

/// <summary>One PATH value's change, as a short diff: what's added, removed, edited or moved, and what isn't.</summary>
public sealed class ValueDiffViewModel
{
    public ValueDiffViewModel(ValueChange change, PathDraft draft, PathDraft baseline)
    {
        var scope = change.Scope;
        Heading = scope == PathScope.Machine ? "SYSTEM PATH" : "USER PATH";
        KindLine = change.Before.Kind != change.After.Kind ? $"{Kind(change.Before.Kind)} → {Kind(change.After.Kind)}" : "";
        LengthLine = $"{N(change.Before.Length)} → {N(change.After.Length)} characters";

        var now = draft.EntriesIn(scope);
        var was = baseline.EntriesIn(scope);
        var lines = new List<DiffLineViewModel>();
        foreach (var e in was.Where(e => draft.Locate(e.Id) is not { } at || at.Scope != scope))
            lines.Add(new("−", EntryWords.Shown(e.Text), Brush("DangerBrush"),
                draft.Locate(e.Id) is { } moved ? $"moved to the {EntryWords.ScopeName(moved.Scope)} PATH" : null));

        // Of the entries in both, the longest run that kept its relative order stayed put; the rest moved.
        var stayed = KeptInOrder(
            was.Where(e => now.Any(n => n.Id == e.Id)).Select(e => e.Id).ToList(),
            now.Where(n => was.Any(e => e.Id == n.Id)).Select(n => n.Id).ToList());
        var unchanged = 0;
        for (var i = 0; i < now.Count; i++)
        {
            var e = now[i];
            var original = was.FirstOrDefault(o => o.Id == e.Id);
            if (original is null)
                lines.Add(new("+", EntryWords.Shown(e.Text), Brush("OkBrush"), e.Origin is null ? null : "moved here"));
            else if (original.Text != e.Text)
                lines.Add(new("~", EntryWords.Shown(e.Text), Brush("AccentBrush"), $"was {EntryWords.Shown(original.Text)}"));
            else if (!stayed.Contains(e.Id))
                lines.Add(new("↕", EntryWords.Shown(e.Text), Brush("AccentBrush"),
                    $"now #{i + 1}, was #{was.Select(x => x.Id).ToList().IndexOf(e.Id) + 1}"));
            else unchanged++;
        }
        Lines = lines;
        UnchangedLine = unchanged == 0 ? "" : $"{PendingChanges.Words(unchanged, "other entry stays", "other entries stay")} as {(unchanged == 1 ? "it is" : "they are")}";
    }

    public string Heading { get; }
    public string KindLine { get; }
    public bool HasKindLine => KindLine.Length > 0;
    public string LengthLine { get; }
    public IReadOnlyList<DiffLineViewModel> Lines { get; }
    public string UnchangedLine { get; }
    public bool HasUnchanged => UnchangedLine.Length > 0;

    /// <summary>The ids of a longest common subsequence of two orderings: the entries that didn't need to move.</summary>
    internal static HashSet<int> KeptInOrder(List<int> before, List<int> after)
    {
        var table = new int[before.Count + 1, after.Count + 1];
        for (var i = before.Count - 1; i >= 0; i--)
            for (var j = after.Count - 1; j >= 0; j--)
                table[i, j] = before[i] == after[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);

        var kept = new HashSet<int>();
        for (int i = 0, j = 0; i < before.Count && j < after.Count;)
        {
            if (before[i] == after[j]) { kept.Add(before[i]); i++; j++; }
            else if (table[i + 1, j] >= table[i, j + 1]) i++;
            else j++;
        }
        return kept;
    }

    static string Kind(PathValueKind kind) => kind switch
    {
        PathValueKind.String => "REG_SZ",
        PathValueKind.ExpandString => "REG_EXPAND_SZ",
        PathValueKind.Missing => "not set",
        _ => "other",
    };

    static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}

public sealed record DiffLineViewModel(string Marker, string Text, IBrush Brush, string? Note)
{
    public bool HasNote => Note is not null;
}

/// <summary>One folder's permission change.</summary>
public sealed class AclChangeViewModel(AclChange change)
{
    public string Path { get; } = change.Path;
    public IReadOnlyList<string> Lines { get; } = change.Lines;
    public string Badge { get; } = change.Refusal is not null ? "can't change" : !change.Writes ? "through inheritance" : change.NeedsAdmin ? "needs admin" : "as you";
    public IBrush BadgeBrush { get; } = change.Refusal is not null ? Brush("DangerBrush") : change.NeedsAdmin ? Severities.BrushFor(Severity.Medium) : Brush("MutedBrush");
    public string Note { get; } =
        change.Refusal ?? (change.CoveredBy is { } cover ? $"Gets its change from {cover} above it, through inheritance." : "");
    public bool HasNote => Note.Length > 0;
}

/// <summary>One command that would resolve differently.</summary>
public sealed class CommandChangeViewModel(CommandChange change)
{
    public string Command { get; } = change.Command;
    public string Before { get; } = change.Before?.FullPath ?? "nothing";
    public string After { get; } = change.After?.FullPath ?? "nothing";
    public IBrush Brush { get; } = change.Builtin ? Severities.BrushFor(Severity.Medium) : Theme.Brush("FgBrush");
}

/// <summary>One write's outcome, as History and the Review page show it.</summary>
public sealed class StepRowViewModel(StepResult step)
{
    public string Target { get; } = step.Target switch
    {
        StepResult.MachineTarget => "System PATH",
        StepResult.UserTarget => "User PATH",
        var path => path,
    };
    public string Status { get; } = step.Status switch
    {
        StepStatus.Applied => "done",
        StepStatus.Failed => "failed",
        _ => "not done",
    };
    public IBrush Brush { get; } = step.Status switch
    {
        StepStatus.Applied => Theme.Brush("OkBrush"),
        StepStatus.Failed => Theme.Brush("DangerBrush"),
        _ => Theme.Brush("MutedBrush"),
    };
    public string Message { get; } = step.Message ?? "";
    public bool HasMessage => Message.Length > 0;
}

/// <summary>How an apply ended, in a sentence.</summary>
internal static class Outcomes
{
    public static string Describe(ChangeRecord record) => record.Outcome switch
    {
        ChangeOutcome.Applied => record.UndoOf is null
            ? "Done. New programs get the new PATH; ones already open keep the old one until restarted. The backup is in History."
            : "Undone. Everything this change touched is back as it was.",
        ChangeOutcome.Partial => "Some of it was applied and some wasn't: see below. What was applied can be undone from History.",
        ChangeOutcome.Cancelled => record.Message ?? "Cancelled: nothing was changed.",
        ChangeOutcome.Refused => record.Message ?? "Refused: nothing was changed.",
        _ => "Nothing was changed. " + (record.Message ?? record.Steps.FirstOrDefault(s => s.Message is not null)?.Message ?? ""),
    };
}
