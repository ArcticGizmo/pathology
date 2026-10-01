using System.ComponentModel;
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

namespace Pathology.App.ViewModels;

/// <summary>
/// What you've staged to change, shared by the System and User pages and the Review page: the fixes you picked,
/// your own edits (move, reorder, edit, delete), and what they add up to. Nothing is staged until you pick it, and
/// nothing is written until Review applies it.
/// </summary>
public sealed class PendingChanges : ObservableObject
{
    readonly IRepairService _repair;

    /// <summary>Fix id → staged, for the fixes you've picked or put back. Survives a re-scan.</summary>
    readonly Dictionary<string, bool> _choices = new(StringComparer.Ordinal);
    readonly List<EntryEdit> _edits = [];

    /// <summary>Entry id → the fixes that change that line; scope → the fixes about the whole value.</summary>
    readonly Dictionary<int, List<FixRowViewModel>> _byEntry = [];
    readonly Dictionary<PathScope, List<FixRowViewModel>> _byScope = [];

    bool _batch;

    public PendingChanges(ScanSession session, IRepairService repair)
    {
        Session = session;
        _repair = repair;
        session.PropertyChanged += OnSessionChanged;
        Load(session.Current);
    }

    public ScanSession Session { get; }
    public IRepairService Repair => _repair;

    /// <summary>The scan everything here is planned against.</summary>
    public ScanResult? Result { get; private set; }

    /// <summary>Every fix PATHology can make for this scan, staged or not.</summary>
    public IReadOnlyList<FixRowViewModel> Fixes { get; private set; } = [];

    /// <summary>Both PATHs as scanned.</summary>
    public PathDraft Base { get; private set; } = new();

    /// <summary>Both PATHs as they'd be once what's staged is applied.</summary>
    public PathDraft Draft { get; private set; } = new();

    /// <summary>The writes Apply would make.</summary>
    public ChangeSet Changes { get; private set; } = new();

    /// <summary>What applying would change, and what that does to the ratings.</summary>
    public PlanViewModel? Plan { get; private set; }

    /// <summary>Raised after anything here changes, so the pages can redraw from it.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<FixRowViewModel> Staged => Fixes.Where(f => f.IsStaged).ToList();
    public int EditCount => _edits.Count;
    public int Count => Fixes.Count(f => f.IsStaged) + _edits.Count;
    public bool HasStaged => Count > 0;

    /// <summary>"2 fixes and 1 edit staged", or "" when nothing is.</summary>
    public string Summary
    {
        get
        {
            var fixes = Fixes.Count(f => f.IsStaged);
            return (fixes, _edits.Count) switch
            {
                (0, 0) => "",
                (_, 0) => $"{Words(fixes, "fix", "fixes")} staged",
                (0, var e) => $"{Words(e, "edit", "edits")} staged",
                var (f, e) => $"{Words(f, "fix", "fixes")} and {Words(e, "edit", "edits")} staged",
            };
        }
    }

    /// <summary>The recommended fixes that aren't staged yet.</summary>
    public int UnstagedRecommended => Fixes.Count(f => f.Fix.Recommended && !f.IsStaged);

    void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanSession.Current)) Load(Session.Current);
    }

    /// <summary>Plan against a new scan: the fixes are worked out again (keeping your picks), and your edits go.</summary>
    void Load(ScanResult? result)
    {
        _edits.Clear();
        Result = result;
        _byEntry.Clear();
        _byScope.Clear();
        if (result is null)
        {
            Fixes = [];
            Base = Draft = new PathDraft();
            Changes = new ChangeSet();
            Plan = null;
            Raise();
            return;
        }

        var suggested = RemediationPlanner.Suggest(result.Diagnosis, result.Context);
        Base = PathDraft.From(result.Snapshot);
        Fixes = suggested.Select(f => new FixRowViewModel(f, _choices.TryGetValue(f.Id, out var on) && on, this)).ToList();
        MapFixes(result);
        Recompute();
    }

    /// <summary>
    /// Which lines each fix changes (its edits' entries, and the entries whose folder it locks down), and which fixes
    /// are about a whole value instead (its kind, its order, entries it puts back), so each shows where it acts.
    /// </summary>
    void MapFixes(ScanResult result)
    {
        var folderOf = Base.All.ToDictionary(e => e.Id, e =>
            e.Origin is { } o && result.Snapshot.EntriesIn(o.Scope).FirstOrDefault(x => x.Index == o.Index) is { } scanned
            && result.Context.Resolve(scanned).Final is { } final ? PathText.Key(final.Path) : null);

        foreach (var row in Fixes)
        {
            var folders = row.Fix.Acls.Select(a => PathText.Key(a.Folder)).ToHashSet(StringComparer.Ordinal);
            var ids = row.Fix.Edits.Select(EntryIdOf).OfType<int>()
                .Concat(folderOf.Where(f => f.Value is not null && folders.Contains(f.Value)).Select(f => f.Key))
                .Distinct()
                .ToList();
            row.Lines = ids.Count;
            foreach (var id in ids) Add(_byEntry, id, row);
            if (ids.Count > 0) continue;
            var scopes = row.Fix.Edits.Select(ScopeOf).OfType<PathScope>().Distinct().ToList();
            // Nothing to pin it to: it goes on the system PATH's heading.
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

    /// <summary>The scope a fix is about, for sending you to it: where its first line or value is.</summary>
    public PathScope ScopeOfFix(SuggestedFix fix)
    {
        foreach (var edit in fix.Edits)
        {
            if (ScopeOf(edit) is { } scope) return scope;
            if (EntryIdOf(edit) is { } id && Base.Locate(id) is { } at) return at.Scope;
        }
        // A lock-down has no edits: where its first folder is.
        foreach (var (id, rows) in _byEntry)
            if (rows.Any(r => ReferenceEquals(r.Fix, fix)) && Base.Locate(id) is { } where)
                return where.Scope;
        return PathScope.Machine;
    }

    /// <summary>The fixes that change this line.</summary>
    public IReadOnlyList<FixRowViewModel> FixesFor(int id) => _byEntry.TryGetValue(id, out var rows) ? rows : [];

    /// <summary>The fixes about a whole value, rather than any one line.</summary>
    public IReadOnlyList<FixRowViewModel> FixesFor(PathScope scope) => _byScope.TryGetValue(scope, out var rows) ? rows : [];

    /// <summary>
    /// The fixes for a problem: the ones meant for it, or failing that, the ones that take every entry it's about
    /// out of where it is. A folder in your profile that's in the system PATH is both writable by you and in the wrong
    /// PATH; its fix is the move to your user PATH, which settles both.
    /// </summary>
    public IReadOnlyList<FixRowViewModel> FixesForProblem(FindingGroup group)
    {
        var meant = Fixes.Where(f => f.Fix.RootCauses.Contains(group.RootCause, StringComparer.Ordinal)).ToList();
        if (meant.Count > 0 || group.Severity == Severity.Info) return meant;

        var ids = group.Members.SelectMany(f => f.Entries).Select(e => IdOf(e.Scope, e.Index)).Distinct().ToList();
        if (ids.Count == 0 || ids.Any(id => id is null)) return [];
        return Fixes.Where(f => ids.All(id => f.Fix.Edits.Any(e => TakesOut(e, id!.Value)))).ToList();

        static bool TakesOut(EntryEdit edit, int id) => edit is RemoveEntry r && r.Id == id || edit is MoveToUser m && m.Id == id;
    }

    /// <summary>The line a scanned entry became.</summary>
    public int? IdOf(PathScope scope, int index) =>
        Base.All.FirstOrDefault(e => e.Origin == new EntryOrigin(scope, index))?.Id;

    /// <summary>An entry's text with its variables expanded, as a new process would see it.</summary>
    public string Expand(string text) =>
        Result is { } result ? EnvironmentExpander.Expand(text, result.Context.Variable).Text : text;

    internal void Toggled(FixRowViewModel row)
    {
        _choices[row.Fix.Id] = row.IsStaged;
        if (!_batch) Recompute();
    }

    /// <summary>Stage one of your own changes to a line.</summary>
    public void Edit(EntryEdit edit)
    {
        _edits.Add(edit);
        Recompute();
    }

    /// <summary>
    /// Move a line to the other PATH: a system entry to the front of your user PATH, a user entry to the end of the
    /// system PATH. When your user PATH already has the system entry, the system copy is removed instead, since moving
    /// it would only make a duplicate. Either way the user PATH is written first when it's applied
    /// (<see cref="ChangeSet.UserFirst"/>).
    /// </summary>
    public void MoveToOtherScope(int id)
    {
        if (Draft.Locate(id) is not { } at || Draft.Find(id) is not { } entry) return;
        if (at.Scope == PathScope.User)
        {
            Edit(new MoveEntry(id, PathScope.Machine, int.MaxValue));
            return;
        }
        var key = PathText.Key(PathText.Strip(entry.Text));
        var already = Draft.User.Any(u => u.Id != id && PathText.Key(PathText.Strip(u.Text)) == key);
        Edit(already ? new RemoveEntry(id) : new MoveToUser(id));
    }

    /// <summary>A line you took out or moved away by hand, which <see cref="PutBack"/> can return.</summary>
    public bool CanPutBack(int id, PathScope from) => _edits.Any(e => TakesAway(e, id, from));

    /// <summary>Undo your own edits that took this line out of <paramref name="from"/>.</summary>
    public void PutBack(int id, PathScope from)
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

    /// <summary>Your own edits to this line (not fixes'), which <see cref="Revert"/> drops.</summary>
    public bool HasEditsTo(int id) => _edits.Any(e => EntryIdOf(e) == id);

    /// <summary>Drop every edit of your own to this line.</summary>
    public void Revert(int id)
    {
        _edits.RemoveAll(e => EntryIdOf(e) == id);
        Recompute();
    }

    /// <summary>Stage every recommended fix.</summary>
    public void StageRecommended() => Batch(() =>
    {
        foreach (var fix in Fixes.Where(f => f.Fix.Recommended)) fix.IsStaged = true;
    });

    /// <summary>Unstage everything: every fix, and every edit of your own.</summary>
    public void Discard() => Batch(() =>
    {
        foreach (var fix in Fixes) fix.IsStaged = false;
        _edits.Clear();
    });

    void Batch(Action change)
    {
        _batch = true;
        try { change(); }
        finally { _batch = false; }
        Recompute();
    }

    /// <summary>Work out the draft, the folder changes, the change set and its outcome from the picks and edits.</summary>
    void Recompute()
    {
        if (Result is not { } result) return;
        var chosen = Fixes.Where(f => f.IsStaged).Select(f => f.Fix).ToList();
        Draft = Base.Apply(chosen.SelectMany(f => f.Edits)).Apply(_edits);
        var acls = AclPlanner.Plan(result.Snapshot, chosen.SelectMany(f => f.Acls), _repair.Designer);
        Changes = ChangeSet.From(result.Snapshot, Draft, acls);
        var outcome = Changes.IsEmpty ? null : _repair.Project(result.Diagnosis, Changes);
        Plan = new PlanViewModel(Changes, outcome, Draft, Base, result);
        Raise();
    }

    void Raise()
    {
        OnPropertyChanged(string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static string Words(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}

/// <summary>One fix PATHology can make, and whether it's staged. Staging it here stages it everywhere it shows.</summary>
public sealed partial class FixRowViewModel : ViewModelBase
{
    readonly PendingChanges _owner;
    readonly bool _ready;

    public FixRowViewModel(SuggestedFix fix, bool staged, PendingChanges owner)
    {
        Fix = fix;
        _owner = owner;
        _isStaged = staged;
        _ready = true;
    }

    public SuggestedFix Fix { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageLabel))]
    private bool _isStaged;

    /// <summary>How many lines it changes, so a lock-down of several folders says so on each.</summary>
    public int Lines { get; internal set; }

    public string Title => Fix.Title;
    public string Problem => Fix.Problem;
    public string? Note => Fix.Note;
    public bool HasNote => Fix.Note is not null;
    public IBrush SeverityBrush => Severities.BrushFor(Fix.Severity);

    /// <summary>"needs admin · 2 lines · not recommended".</summary>
    public string Meta => string.Join(" · ", new[]
    {
        Fix.NeedsAdmin ? "needs admin" : "",
        Lines > 1 ? $"{Lines} lines" : "",
        Fix.Recommended ? "" : "changes which tool runs, so it's your call",
    }.Where(s => s.Length > 0));
    public bool HasMeta => Meta.Length > 0;

    public string StageLabel => IsStaged ? "Staged ✓" : "Stage fix";

    [RelayCommand]
    private void Toggle() => IsStaged = !IsStaged;

    partial void OnIsStagedChanged(bool value)
    {
        if (_ready) _owner.Toggled(this);
    }
}
