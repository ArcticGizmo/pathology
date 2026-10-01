using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Repair;
using Pathology.App.Scanning;
using Pathology.App.Theming;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Normalisation;
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
    (int Id, bool Ghost)? _selected;

    /// <summary>Entry id → the fixes that change that line; scope → the fixes about the whole value.</summary>
    readonly Dictionary<int, List<FixRowViewModel>> _byEntry = [];
    readonly Dictionary<PathScope, List<FixRowViewModel>> _byScope = [];
    readonly Dictionary<FixRowViewModel, int> _lines = [];

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
    public string FixesHeading => Fixes.Count == 0
        ? "Nothing here has an automatic fix, but you can still change the PATH by hand."
        : $"{Fixes.Count(f => f.IsSelected)} of {DraftSectionViewModel.Words(Fixes.Count, "fix", "fixes")} ticked.";

    /// <summary>The change set the page shows: what Apply would write.</summary>
    public ChangeSet Changes => _changes;

    /// <summary>The line another page sent you to. Changes only on arrival, so the view scrolls to it then and not on every tick.</summary>
    public DraftRowViewModel? ArrivedRow => Sections.SelectMany(s => s.Rows).FirstOrDefault(r => r.IsSelected);

    protected override void Rebuild(ScanResult? result)
    {
        _edits.Clear();
        _nextId = PathDraft.ManualIdBase;
        _selected = null;
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
        MapFixes(result);
        Recompute();
    }

    /// <summary>
    /// Which lines each fix changes (its edits' entries, and the entries whose folder it locks down), and which fixes
    /// are about a whole value instead (its kind, its order, entries it adds), so each shows where it acts.
    /// </summary>
    void MapFixes(ScanResult result)
    {
        _byEntry.Clear();
        _byScope.Clear();
        _lines.Clear();
        var folderOf = _base.All.ToDictionary(e => e.Id, e =>
            e.Origin is { } o && result.Snapshot.EntriesIn(o.Scope).FirstOrDefault(x => x.Index == o.Index) is { } scanned
            && result.Context.Resolve(scanned).Final is { } final ? PathText.Key(final.Path) : null);

        foreach (var row in Fixes)
        {
            var folders = row.Fix.Acls.Select(a => PathText.Key(a.Folder)).ToHashSet(StringComparer.Ordinal);
            var ids = row.Fix.Edits.Select(EntryIdOf).OfType<int>()
                .Concat(folderOf.Where(f => f.Value is not null && folders.Contains(f.Value)).Select(f => f.Key))
                .Distinct()
                .ToList();
            _lines[row] = ids.Count;
            foreach (var id in ids) Add(_byEntry, id, row);
            if (ids.Count > 0) continue;
            var scopes = row.Fix.Edits.Select(ScopeOf).OfType<PathScope>().Distinct().ToList();
            // Nothing to pin it to: it goes at the top.
            foreach (var scope in scopes.Count > 0 ? scopes : [PathScope.Machine]) Add(_byScope, scope, row);
        }

        static void Add<TKey>(Dictionary<TKey, List<FixRowViewModel>> map, TKey key, FixRowViewModel row) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(row);
        }
    }

    static int? EntryIdOf(EntryEdit edit) => edit switch
    {
        RemoveEntry e => e.Id,
        ReplaceText e => e.Id,
        TidyText e => e.Id,
        MoveEntry e => e.Id,
        MoveToUser e => e.Id,
        _ => null,
    };

    static PathScope? ScopeOf(EntryEdit edit) => edit switch
    {
        SetKind e => e.Scope,
        Reorder e => e.Scope,
        AddEntry e => e.Scope,
        _ => null,
    };

    /// <summary>The fixes that change this line.</summary>
    internal IReadOnlyList<FixTickViewModel> TicksFor(int id) =>
        _byEntry.TryGetValue(id, out var rows) ? rows.Select(r => new FixTickViewModel(r, _lines[r])).ToList() : [];

    /// <summary>The fixes about the whole value, rather than any one line.</summary>
    internal IReadOnlyList<FixTickViewModel> TicksFor(PathScope scope) =>
        _byScope.TryGetValue(scope, out var rows) ? rows.Select(r => new FixTickViewModel(r, 0)).ToList() : [];

    /// <summary>An entry's text with its variables expanded, as a new process would see it.</summary>
    internal string Expand(string text) =>
        Result is { } result ? EnvironmentExpander.Expand(text, result.Context.Variable).Text : text;

    internal bool IsSelected(int id, bool ghost) => _selected == (id, ghost);

    /// <summary>Pick a line out: its editing buttons and its fixes' notes show.</summary>
    internal void Select(DraftRowViewModel row)
    {
        _selected = (row.Entry.Id, row.IsGhost);
        foreach (var r in Sections.SelectMany(s => s.Rows)) r.IsSelected = IsSelected(r.Entry.Id, r.IsGhost);
    }

    /// <summary>A line you took out or moved away by hand, which <see cref="PutBack"/> can return.</summary>
    internal bool CanPutBack(int id, PathScope from) => _edits.Any(e => TakesAway(e, id, from));

    /// <summary>Undo your own edits that took this line out of <paramref name="from"/>.</summary>
    internal void PutBack(int id, PathScope from)
    {
        _edits.RemoveAll(e => TakesAway(e, id, from));
        Recompute();
    }

    static bool TakesAway(EntryEdit edit, int id, PathScope from) => edit switch
    {
        RemoveEntry e => e.Id == id,
        MoveToUser e => e.Id == id && from == PathScope.Machine,
        MoveEntry e => e.Id == id && e.Scope != from,
        _ => false,
    };

    /// <summary>The scanned entry an origin names, as a live line in the editor.</summary>
    void SelectOrigin(PathScope scope, int index)
    {
        _highlight = (scope, index);
        if (_base.All.FirstOrDefault(e => e.Origin == new EntryOrigin(scope, index)) is { } entry) _selected = (entry.Id, false);
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
        SelectOrigin(scope, index);
        Recompute();
        OnPropertyChanged(nameof(ArrivedRow));
    }

    /// <summary>
    /// Arrive from an entry's "Move to your user PATH": add the move to the editor and show it. When your user PATH
    /// already has the entry, the machine copy is removed instead, as the planner does: moving it would only make a
    /// duplicate. Either way the user PATH is written first when it's applied (<see cref="ChangeSet.UserFirst"/>).
    /// </summary>
    public void StageMoveToUser(int machineIndex)
    {
        SelectOrigin(PathScope.Machine, machineIndex);
        var origin = new EntryOrigin(PathScope.Machine, machineIndex);
        if (_base.Machine.FirstOrDefault(e => e.Origin == origin) is { } entry)
        {
            var draft = Draft();
            var key = PathText.Key(PathText.Strip(entry.Text));
            var already = draft.User.Any(u => u.Id != entry.Id && PathText.Key(PathText.Strip(u.Text)) == key);
            _edits.Add(already ? new RemoveEntry(entry.Id) : new MoveToUser(entry.Id));
        }
        Recompute();
        OnPropertyChanged(nameof(ArrivedRow));
    }

    PathDraft Draft() => _base.Apply(Fixes.Where(f => f.IsSelected).SelectMany(f => f.Fix.Edits)).Apply(_edits);

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

/// <summary>
/// One scope of the PATH as it would be, each line with the fixes that change it. A line that's gone (removed, or
/// moved to the other scope) stays where it was, struck through, so its fix can be unticked right there.
/// </summary>
public sealed class DraftSectionViewModel
{
    public DraftSectionViewModel(PathScope scope, PathDraft draft, PathDraft baseline, FixViewModel owner, (PathScope, int)? highlight)
    {
        Scope = scope;
        Heading = scope == PathScope.Machine ? "MACHINE PATH" : "USER PATH";
        ValueFixes = owner.TicksFor(scope);

        var entries = draft.EntriesIn(scope);
        bool Highlighted(DraftEntry e) => highlight is { } h && e.Origin == new EntryOrigin(h.Item1, h.Item2);
        var live = entries.Select((e, i) => new DraftRowViewModel(e, scope, i, entries.Count, draft, baseline, owner, Highlighted(e))).ToList();

        // Each gone line follows the last line before it (in the scan) that's still here.
        var after = new Dictionary<int, List<DraftRowViewModel>>();
        var rows = new List<DraftRowViewModel>();
        int? anchor = null;
        foreach (var e in baseline.EntriesIn(scope))
        {
            if (draft.Locate(e.Id) is { } at && at.Scope == scope) { anchor = e.Id; continue; }
            var ghost = new DraftRowViewModel(e, scope, -1, 0, draft, baseline, owner, Highlighted(e) && draft.Locate(e.Id) is null);
            if (anchor is not { } a) rows.Add(ghost);
            else if (after.TryGetValue(a, out var list)) list.Add(ghost);
            else after[a] = [ghost];
        }
        foreach (var row in live)
        {
            rows.Add(row);
            if (after.TryGetValue(row.Entry.Id, out var gone)) rows.AddRange(gone);
        }
        Rows = rows;
        LiveRows = live;

        Summary = draft.KindOf(scope) switch
        {
            PathValueKind.Missing => "not set",
            PathValueKind.String => "REG_SZ",
            PathValueKind.ExpandString => "REG_EXPAND_SZ",
            _ => "not a string value",
        } + $" · {Words(live.Count, "entry", "entries")}";
    }

    public PathScope Scope { get; }
    public string Heading { get; }
    public string Summary { get; }

    /// <summary>Every line shown: the entries it will have, and the gone ones where they were.</summary>
    public IReadOnlyList<DraftRowViewModel> Rows { get; }

    /// <summary>Just the entries it will have.</summary>
    public IReadOnlyList<DraftRowViewModel> LiveRows { get; }
    public bool HasRows => Rows.Count > 0;

    /// <summary>Fixes about the whole value: its kind, its order, entries put back.</summary>
    public IReadOnlyList<FixTickViewModel> ValueFixes { get; }
    public bool HasValueFixes => ValueFixes.Count > 0;

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

/// <summary>
/// One line of the PATH as it would be: its text, the fixes that change it, and (once it's picked out) the editor's
/// buttons. A <see cref="IsGhost"/> line is one that's gone from this scope, shown where it was.
/// </summary>
public sealed partial class DraftRowViewModel : ViewModelBase
{
    readonly FixViewModel _owner;
    readonly int _count;

    /// <param name="position">Its place in the scope, or -1 for a line that's gone.</param>
    public DraftRowViewModel(DraftEntry entry, PathScope scope, int position, int count, PathDraft draft, PathDraft baseline,
        FixViewModel owner, bool highlighted)
    {
        Entry = entry;
        Scope = scope;
        Position = position;
        _count = count;
        _owner = owner;
        _editText = entry.Text;
        IsHighlighted = highlighted;
        IsGhost = position < 0;
        _isSelected = owner.IsSelected(entry.Id, IsGhost);
        Fixes = owner.TicksFor(entry.Id);
        Notes = Fixes.Where(t => t.Fix.HasNote).Select(t => t.Fix.Note!).Distinct().ToList();
        var expanded = owner.Expand(entry.Text);
        Expanded = string.Equals(expanded, entry.Text, StringComparison.Ordinal) ? "" : expanded;
        CanPutBack = IsGhost && owner.CanPutBack(entry.Id, scope);

        var original = baseline.Find(entry.Id);
        (Status, StatusBrush) = original switch
        {
            _ when IsGhost => draft.Locate(entry.Id) is { } moved
                ? ($"moves to the {(moved.Scope == PathScope.Machine ? "machine" : "user")} PATH", Brush("AccentBrush"))
                : ("removed", Brush("DangerBrush")),
            null => ("added", Brush("OkBrush")),
            _ when entry.Origin is { } o && o.Scope != scope => ($"moved here from the {(o.Scope == PathScope.Machine ? "machine" : "user")} PATH", Brush("AccentBrush")),
            _ when original.Text != entry.Text => ($"was {DraftSectionViewModel.Shown(original.Text)}", Brush("AccentBrush")),
            _ => ("", Brush("MutedBrush")),
        };
    }

    public DraftEntry Entry { get; }
    public PathScope Scope { get; }
    public int Position { get; }
    public bool IsGhost { get; }
    public string Number => IsGhost ? "" : $"#{Position + 1}";

    /// <summary>As it will be stored (defects are picked out in the view).</summary>
    public string Text => Entry.Text;

    /// <summary>What it expands to, or "" when that's the same.</summary>
    public string Expanded { get; }
    public bool HasExpanded => Expanded.Length > 0;

    public string Status { get; }
    public IBrush StatusBrush { get; }
    public bool HasStatus => Status.Length > 0;
    public bool IsHighlighted { get; }

    /// <summary>The fixes that change this line. Ticking one here ticks it everywhere it shows.</summary>
    public IReadOnlyList<FixTickViewModel> Fixes { get; }
    public bool HasFixes => Fixes.Count > 0;

    /// <summary>Its fixes' side effects, shown once the line is picked out.</summary>
    public IReadOnlyList<string> Notes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTools), nameof(ShowNotes))]
    private bool _isSelected;

    /// <summary>The editor's buttons: only on the picked-out line, and never on one that's gone.</summary>
    public bool ShowTools => IsSelected && !IsGhost;
    public bool ShowNotes => IsSelected && Notes.Count > 0;

    /// <summary>You took it out (or moved it away) by hand, so it can be put back.</summary>
    public bool CanPutBack { get; }

    [RelayCommand]
    private void Select() => _owner.Select(this);

    [RelayCommand]
    private void PutBack() => _owner.PutBack(Entry.Id, Scope);
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

/// <summary>A fix's tick box where it shows: on a line it changes, or on the value it's about.</summary>
/// <param name="lines">How many lines it changes, so a lock-down of several folders says so on each.</param>
public sealed class FixTickViewModel(FixRowViewModel fix, int lines)
{
    public FixRowViewModel Fix { get; } = fix;
    public string Title => Fix.Title;
    public IBrush SeverityBrush => Fix.SeverityBrush;
    public string Meta { get; } = Severities.Word(fix.Fix.Severity) + (lines > 1 ? $" · {lines} lines" : "") + (fix.Fix.NeedsAdmin ? " · admin" : "");
    public string Tip { get; } = string.Join(Environment.NewLine,
        new[] { fix.Problem, fix.Meta, fix.Note }.Where(s => !string.IsNullOrEmpty(s)));
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
            : $"One UAC prompt, for {string.Join(" and ", admin)}."
              + (changes.UserFirst() is null ? ""
                  : " Your user PATH is written first, before the prompt, so a moved entry is never missing from both; if you decline, it's put back.");
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
