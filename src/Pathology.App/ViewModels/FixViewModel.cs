using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Repair;
using Pathology.App.Scanning;
using Pathology.App.Theming;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Remediation;
using static Pathology.App.Theme;

namespace Pathology.App.ViewModels;

/// <summary>
/// The Fix page: what PATHology can fix, the PATH as it would be (editable), and what changes, before anything is
/// written. It's a dry run until Apply is clicked and confirmed.
/// </summary>
public sealed partial class FixViewModel : ScanPageViewModel
{
    readonly IRepairService _repair;
    readonly Action? _applied;

    /// <summary>Fix id → ticked, for the fixes you've changed from their default. Survives a re-scan.</summary>
    readonly Dictionary<string, bool> _choices = new(StringComparer.Ordinal);
    readonly List<EntryEdit> _edits = [];
    int _nextId = PathDraft.ManualIdBase;

    IReadOnlyList<SuggestedFix> _suggested = [];
    PathDraft _base = new();
    ChangeSet _changes = new();
    (PathScope Scope, int Index)? _highlight;

    /// <param name="applied">Called after an apply wrote something (History refreshes).</param>
    public FixViewModel(ScanSession session, INavigator navigator, IRepairService repair, Action? applied = null)
        : base(session, navigator)
    {
        _repair = repair;
        _applied = applied;
        Rebuild(session.Current);
    }

    public override string Title => "Fix";

    [ObservableProperty] private IReadOnlyList<FixRowViewModel> _fixes = [];
    [ObservableProperty] private IReadOnlyList<DraftSectionViewModel> _sections = [];
    [ObservableProperty] private PlanViewModel? _plan;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newEntryText = "";

    /// <summary>Add the new entry to your user PATH (true) or the machine PATH (false).</summary>
    [ObservableProperty] private bool _newEntryInUser = true;

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
    public bool HasFixes => Fixes.Count > 0;
    public bool HasEdits => _edits.Count > 0;
    public string FixesHeading => Fixes.Count == 0 ? "NOTHING TO FIX AUTOMATICALLY" : $"FIXES ({Fixes.Count(f => f.IsSelected)} OF {Fixes.Count} CHOSEN)";

    /// <summary>The change set the page shows: what Apply would write.</summary>
    public ChangeSet Changes => _changes;

    protected override void Rebuild(ScanResult? result)
    {
        _edits.Clear();
        _nextId = PathDraft.ManualIdBase;
        IsConfirming = false;
        if (result is null)
        {
            _suggested = [];
            Fixes = [];
            Sections = [];
            Plan = null;
            _changes = new ChangeSet();
            NavCount = 0;
            Refresh();
            return;
        }

        _suggested = RemediationPlanner.Suggest(result.Diagnosis, result.Context);
        _base = PathDraft.From(result.Snapshot);
        Fixes = _suggested.Select(f => new FixRowViewModel(f, _choices.TryGetValue(f.Id, out var on) ? on : f.Recommended, this)).ToList();
        NavCount = _suggested.Count(f => f.Recommended);
        Recompute();
    }

    internal void Toggled(FixRowViewModel row)
    {
        _choices[row.Fix.Id] = row.IsSelected;
        Recompute();
    }

    internal void Edit(EntryEdit edit)
    {
        _edits.Add(edit);
        Recompute();
    }

    /// <summary>Arrive from an entry: show it in the editor.</summary>
    public void Show(PathScope scope, int index)
    {
        _highlight = (scope, index);
        Recompute();
    }

    /// <summary>Work out the draft, the folder changes, the change set and its outcome from the choices and edits.</summary>
    void Recompute()
    {
        if (Result is not { } result) return;
        var chosen = Fixes.Where(f => f.IsSelected).Select(f => f.Fix).ToList();
        var draft = _base.Apply(chosen.SelectMany(f => f.Edits)).Apply(_edits);
        var acls = AclPlanner.Plan(result.Snapshot, chosen.SelectMany(f => f.Acls), _repair.Designer);
        _changes = ChangeSet.From(result.Snapshot, draft, acls);

        var outcome = _changes.IsEmpty ? null : _repair.Project(result.Diagnosis, _changes);
        Sections = [new(PathScope.Machine, draft, _base, this, _highlight), new(PathScope.User, draft, _base, this, _highlight)];
        Plan = new PlanViewModel(_changes, outcome, draft, _base, result);
        Refresh();
    }

    void Refresh()
    {
        OnPropertyChanged(nameof(HasFixes));
        OnPropertyChanged(nameof(HasEdits));
        OnPropertyChanged(nameof(FixesHeading));
        ApplyCommand.NotifyCanExecuteChanged();
        AddCommand.NotifyCanExecuteChanged();
        ResetEditsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var scope = NewEntryInUser ? PathScope.User : PathScope.Machine;
        Edit(new AddEntry(_nextId++, scope, int.MaxValue, NewEntryText.Trim()));
        NewEntryText = "";
    }

    bool CanAdd() => NewEntryText.Trim().Length > 0 && Result is not null;

    [RelayCommand(CanExecute = nameof(HasEdits))]
    private void ResetEdits()
    {
        _edits.Clear();
        Recompute();
    }

    /// <summary>Sort a scope into the recommended order: Windows, then locked down, then writable.</summary>
    [RelayCommand]
    private void RecommendedOrder(DraftSectionViewModel section)
    {
        if (Result is not { } result) return;
        var chosen = Fixes.Where(f => f.IsSelected).Select(f => f.Fix).ToList();
        var draft = _base.Apply(chosen.SelectMany(f => f.Edits)).Apply(_edits);
        var locked = chosen.SelectMany(f => f.Acls).Where(a => a.StripWrite).Select(a => a.Folder);
        Edit(RemediationPlanner.RecommendedOrder(draft, section.Scope, result.Context, locked));
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply() => IsConfirming = true;

    bool CanApply() => !IsApplying && !IsConfirming && Plan is { HasChanges: true, HasProblems: false };

    [RelayCommand]
    private void CancelApply() => IsConfirming = false;

    /// <summary>Apply what's shown: the one path from this page to the writers.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task Confirm()
    {
        var changes = _changes;
        var fixes = Fixes.Where(f => f.IsSelected).Select(f => f.Fix.Title).ToList();
        var summary = Summary(fixes.Count, _edits.Count);
        IsApplying = true;
        try
        {
            var record = await AppServices.RunAsync(() => _repair.Apply(changes, summary, fixes));
            Report(record);
            if (record.Applied.Any())
            {
                _applied?.Invoke();
                await Session.ScanAsync();
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

    static string Summary(int fixes, int edits) =>
        (fixes, edits) switch
        {
            (0, _) => $"{edits} {(edits == 1 ? "edit" : "edits")}",
            (_, 0) => $"{fixes} {(fixes == 1 ? "fix" : "fixes")}",
            _ => $"{fixes} {(fixes == 1 ? "fix" : "fixes")} and {edits} {(edits == 1 ? "edit" : "edits")}",
        };
}

/// <summary>One suggested fix, with its tick box.</summary>
public sealed partial class FixRowViewModel : ViewModelBase
{
    readonly FixViewModel _owner;
    readonly bool _ready;

    public FixRowViewModel(SuggestedFix fix, bool selected, FixViewModel owner)
    {
        Fix = fix;
        _owner = owner;
        _isSelected = selected;
        _ready = true;
    }

    public SuggestedFix Fix { get; }

    [ObservableProperty] private bool _isSelected;

    public string Title => Fix.Title;
    public string Problem => Fix.Problem;
    public string? Note => Fix.Note;
    public bool HasNote => Fix.Note is not null;
    public IBrush SeverityBrush => Severities.BrushFor(Fix.Severity);
    public string Meta => $"{Severities.Word(Fix.Severity)} · {Fix.Category.ToString().ToLowerInvariant()}" +
                          (Fix.NeedsAdmin ? " · needs admin" : "") + (Fix.Recommended ? "" : " · not ticked by default");

    partial void OnIsSelectedChanged(bool value)
    {
        if (_ready) _owner.Toggled(this);
    }
}

/// <summary>One scope of the PATH as it would be.</summary>
public sealed class DraftSectionViewModel
{
    public DraftSectionViewModel(PathScope scope, PathDraft draft, PathDraft baseline, FixViewModel owner, (PathScope, int)? highlight)
    {
        Scope = scope;
        Heading = scope == PathScope.Machine ? "MACHINE PATH" : "USER PATH";
        var entries = draft.EntriesIn(scope);
        Rows = entries.Select((e, i) => new DraftRowViewModel(e, scope, i, entries.Count, baseline, owner,
            highlight is { } h && e.Origin == new EntryOrigin(h.Item1, h.Item2))).ToList();
        Removed = baseline.EntriesIn(scope)
            .Where(e => draft.Locate(e.Id) is null)
            .Select(e => Shown(e.Text))
            .ToList();
        Summary = draft.KindOf(scope) switch
        {
            PathValueKind.Missing => "not set",
            PathValueKind.String => "REG_SZ",
            PathValueKind.ExpandString => "REG_EXPAND_SZ",
            _ => "not a string value",
        } + $" · {Words(Rows.Count, "entry", "entries")}";
    }

    public PathScope Scope { get; }
    public string Heading { get; }
    public string Summary { get; }
    public IReadOnlyList<DraftRowViewModel> Rows { get; }
    public bool HasRows => Rows.Count > 0;

    /// <summary>The scanned entries that are gone from this scope (removed, or moved to the other).</summary>
    public IReadOnlyList<string> Removed { get; }
    public bool HasRemoved => Removed.Count > 0;

    internal static string Words(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>An entry's text as shown: "(empty)" for nothing, and edge spaces as <c>·</c>, so a trimmed space is visible.</summary>
    internal static string Shown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(empty)";
        var lead = text.Length - text.TrimStart().Length;
        var trail = text.Length - text.TrimEnd().Length;
        return new string('·', lead) + text.Trim() + new string('·', trail);
    }
}

/// <summary>One entry of the PATH as it would be, with the editor's buttons.</summary>
public sealed partial class DraftRowViewModel : ViewModelBase
{
    readonly FixViewModel _owner;
    readonly int _count;

    public DraftRowViewModel(DraftEntry entry, PathScope scope, int position, int count, PathDraft baseline, FixViewModel owner, bool highlighted)
    {
        Entry = entry;
        Scope = scope;
        Position = position;
        _count = count;
        _owner = owner;
        _editText = entry.Text;
        IsHighlighted = highlighted;

        var original = baseline.Find(entry.Id);
        (Status, StatusBrush) = original switch
        {
            null => ("added", Brush("OkBrush")),
            _ when entry.Origin is { } o && o.Scope != scope => ($"moved here from the {(o.Scope == PathScope.Machine ? "machine" : "user")} PATH", Brush("AccentBrush")),
            _ when original.Text != entry.Text => ($"was {DraftSectionViewModel.Shown(original.Text)}", Brush("AccentBrush")),
            _ => ("", Brush("MutedBrush")),
        };
    }

    public DraftEntry Entry { get; }
    public PathScope Scope { get; }
    public int Position { get; }
    public string Number => $"#{Position + 1}";
    public string Text => DraftSectionViewModel.Shown(Entry.Text);
    public string Status { get; }
    public IBrush StatusBrush { get; }
    public bool HasStatus => Status.Length > 0;
    public bool IsHighlighted { get; }
    public string MoveLabel => Scope == PathScope.Machine ? "To user" : "To machine";
    public string MoveTip => Scope == PathScope.Machine
        ? "Move it to the front of your user PATH"
        : "Move it to the end of the machine PATH (needs admin)";

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _owner.Edit(new MoveEntry(Entry.Id, Scope, Position - 1));

    bool CanMoveUp() => Position > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _owner.Edit(new MoveEntry(Entry.Id, Scope, Position + 1));

    bool CanMoveDown() => Position < _count - 1;

    [RelayCommand]
    private void Remove() => _owner.Edit(new RemoveEntry(Entry.Id));

    /// <summary>Machine → the front of your user PATH; user → the end of the machine PATH. Both keep it nearest where it was.</summary>
    [RelayCommand]
    private void MoveScope() => _owner.Edit(Scope == PathScope.Machine
        ? new MoveToUser(Entry.Id)
        : new MoveEntry(Entry.Id, PathScope.Machine, int.MaxValue));

    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Entry.Text;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveEdit()
    {
        IsEditing = false;
        if (EditText != Entry.Text) _owner.Edit(new ReplaceText(Entry.Id, EditText));
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;
}

/// <summary>The right-hand column: what Apply would change, and what that does.</summary>
public sealed class PlanViewModel
{
    const int MaxCommands = 40;

    public PlanViewModel(ChangeSet changes, PlanOutcome? outcome, PathDraft draft, PathDraft baseline, ScanResult result)
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
        ResolvedLine = resolved == 0 ? "" : $"Fixes {DraftSectionViewModel.Words(resolved, "problem", "problems")}.";
        Introduced = (outcome?.Introduced ?? []).Where(g => g.Severity > Severity.Info)
            .Select(g => new IntroducedViewModel(g.Primary.Title, Severities.BrushFor(g.Severity)))
            .ToList();

        var admin = new List<string>();
        if (changes.ValueFor(PathScope.Machine) is not null) admin.Add("the machine PATH");
        var adminFolders = changes.Acls.Count(a => a.Writes && a.NeedsAdmin);
        if (adminFolders > 0) admin.Add(DraftSectionViewModel.Words(adminFolders, "folder", "folders"));
        AdminLine = !HasChanges ? ""
            : admin.Count == 0 ? "No UAC prompt: everything here is yours to change."
            : $"One UAC prompt, for {string.Join(" and ", admin)}.";
    }

    public bool HasChanges { get; }
    public IReadOnlyList<string> Problems { get; }
    public bool HasProblems => Problems.Count > 0;

    public IReadOnlyList<RatingChangeViewModel> Ratings { get; }
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
        Heading = scope == PathScope.Machine ? "MACHINE PATH" : "USER PATH";
        KindLine = change.Before.Kind != change.After.Kind ? $"{Kind(change.Before.Kind)} → {Kind(change.After.Kind)}" : "";
        LengthLine = $"{N(change.Before.Length)} → {N(change.After.Length)} characters";

        var now = draft.EntriesIn(scope);
        var was = baseline.EntriesIn(scope);
        var lines = new List<DiffLineViewModel>();
        foreach (var e in was.Where(e => draft.Locate(e.Id) is not { } at || at.Scope != scope))
            lines.Add(new("−", DraftSectionViewModel.Shown(e.Text), Brush("DangerBrush"),
                draft.Locate(e.Id) is { } moved ? $"moved to the {(moved.Scope == PathScope.Machine ? "machine" : "user")} PATH" : null));

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
                lines.Add(new("+", DraftSectionViewModel.Shown(e.Text), Brush("OkBrush"), e.Origin is null ? null : "moved here"));
            else if (original.Text != e.Text)
                lines.Add(new("~", DraftSectionViewModel.Shown(e.Text), Brush("AccentBrush"), $"was {DraftSectionViewModel.Shown(original.Text)}"));
            else if (!stayed.Contains(e.Id))
                lines.Add(new("↕", DraftSectionViewModel.Shown(e.Text), Brush("AccentBrush"),
                    $"now #{i + 1}, was #{was.Select(x => x.Id).ToList().IndexOf(e.Id) + 1}"));
            else unchanged++;
        }
        Lines = lines;
        UnchangedLine = unchanged == 0 ? "" : $"{DraftSectionViewModel.Words(unchanged, "other entry stays", "other entries stay")} as {(unchanged == 1 ? "it is" : "they are")}";
    }

    public string Heading { get; }
    public string KindLine { get; }
    public bool HasKindLine => KindLine.Length > 0;
    public string LengthLine { get; }
    public IReadOnlyList<DiffLineViewModel> Lines { get; }
    public string UnchangedLine { get; }
    public bool HasUnchanged => UnchangedLine.Length > 0;

    /// <summary>The ids of a longest common subsequence of two orderings: the entries that didn't need to move.</summary>
    static HashSet<int> KeptInOrder(List<int> before, List<int> after)
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

/// <summary>One write's outcome, as History and the Fix page show it.</summary>
public sealed class StepRowViewModel(StepResult step)
{
    public string Target { get; } = step.Target switch
    {
        StepResult.MachineTarget => "Machine PATH",
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
