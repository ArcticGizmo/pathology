using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Controls;
using Pathology.App.Scanning;
using Pathology.App.Theming;
using Pathology.Core.Detection;
using Pathology.Core.Learn;
using Pathology.Core.Model;
using Pathology.Core.Remediation;
using static Pathology.App.Theme;

namespace Pathology.App.ViewModels;

/// <summary>
/// One PATH, system or user, as it would be once what's staged is applied: each entry in the order Windows searches
/// it. Picking an entry shows its problems, what can be done about each, and the moves, edits and deletions you can
/// make yourself. Everything is staged; the pending bar takes you to Review to apply it.
/// </summary>
public sealed partial class EntriesViewModel : ScanPageViewModel
{
    (int Id, bool Ghost)? _key;

    public EntriesViewModel(PathScope scope, ScanSession session, INavigator navigator, PendingChanges pending)
        : base(session, navigator)
    {
        Scope = scope;
        Pending = pending;
        pending.Changed += (_, _) => Refresh();
        Refresh();
    }

    public PathScope Scope { get; }
    public PendingChanges Pending { get; }

    public override string Title => Scope == PathScope.Machine ? "System" : "User";
    public override bool IsNested => true;

    public string Heading => Scope == PathScope.Machine ? "System PATH" : "User PATH";

    public string Help => Scope == PathScope.Machine
        ? "Every account on this PC gets these, searched before your own. Pick an entry to see what's wrong with it and what can be done. Changes are staged until you review and apply them."
        : "Yours alone, searched after the system PATH. Pick an entry to see what's wrong with it and what can be done. Changes are staged until you review and apply them.";

    [ObservableProperty] private IReadOnlyList<EntryRowViewModel> _rows = [];
    [ObservableProperty] private IReadOnlyList<FixRowViewModel> _valueFixes = [];
    [ObservableProperty] private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private EntryRowViewModel? _selected;

    [ObservableProperty] private EntryPanelViewModel? _panel;

    /// <summary>The panel's folded-away details are open. Kept for the page, so it stays open from entry to entry.</summary>
    [ObservableProperty] private bool _showDetails;

    public bool HasSelection => Selected is not null;
    public bool HasRows => Rows.Count > 0;
    public bool HasValueFixes => ValueFixes.Count > 0;
    public string ValueHeading => $"THE WHOLE {Heading.ToUpperInvariant()}";

    // The pending bar.
    public bool HasStaged => Pending.HasStaged;
    public string PendingSummary => Pending.HasStaged ? Pending.Summary : "Nothing staged.";
    public IReadOnlyList<RatingChangeViewModel> RatingChanges => Pending.Plan?.RatingChanges ?? [];
    public bool HasRatingChanges => Pending.HasStaged && Pending.Plan is { HasRatingChanges: true };
    public bool CanStageRecommended => Pending.UnstagedRecommended > 0;
    public string StageRecommendedLabel => Pending.UnstagedRecommended == 1
        ? "Stage the recommended fix"
        : $"Stage the {Pending.UnstagedRecommended} recommended fixes";

    /// <summary>Scan results arrive through <see cref="PendingChanges"/>, which re-plans first and then tells every page.</summary>
    protected override void Rebuild(ScanResult? result) { }

    void Refresh()
    {
        var result = Pending.Result;
        if (result is null)
        {
            Rows = [];
            ValueFixes = [];
            Summary = "";
            NavCount = 0;
            Selected = null;
            RaiseBar();
            return;
        }

        var draft = Pending.Draft;
        var entries = draft.EntriesIn(Scope);
        // Of the entries here before and after, the longest run that kept its order stayed put; only the rest moved.
        var was = Pending.Base.EntriesIn(Scope).Select(e => e.Id).ToList();
        var now = entries.Select(e => e.Id).ToList();
        var stayed = ValueDiffViewModel.KeptInOrder(was.Where(now.Contains).ToList(), now.Where(was.Contains).ToList());
        var live = entries.Select((e, i) => new EntryRowViewModel(e, Scope, i, entries.Count, this, moved: was.Contains(e.Id) && !stayed.Contains(e.Id))).ToList();

        // A line that's gone (removed, or moved to the other PATH) stays where it was, struck through, after the last
        // line before it (in the scan) that's still here.
        var after = new Dictionary<int, List<EntryRowViewModel>>();
        var rows = new List<EntryRowViewModel>();
        int? anchor = null;
        foreach (var e in Pending.Base.EntriesIn(Scope))
        {
            if (draft.Locate(e.Id) is { } at && at.Scope == Scope) { anchor = e.Id; continue; }
            var ghost = new EntryRowViewModel(e, Scope, -1, 0, this, moved: false);
            if (anchor is not { } a) rows.Add(ghost);
            else if (after.TryGetValue(a, out var list)) list.Add(ghost);
            else after[a] = [ghost];
        }
        foreach (var row in live)
        {
            rows.Add(row);
            if (after.TryGetValue(row.Entry.Id, out var gone)) rows.AddRange(gone);
        }
        // (The empty slot Windows' own trailing ';' leaves isn't in the draft: it isn't an entry.)
        Rows = rows;

        ValueFixes = Pending.FixesFor(Scope);
        Summary = draft.KindOf(Scope) switch
        {
            PathValueKind.Missing => "not set",
            PathValueKind.String => "REG_SZ",
            PathValueKind.ExpandString => "REG_EXPAND_SZ",
            _ => "not a string value, so Windows ignores it",
        } + $" · {PendingChanges.Words(live.Count, "entry", "entries")}";
        NavCount = result.Snapshot.EntriesIn(Scope).Count(e => result.GroupsFor(e).Any(g => g.Severity > Severity.Info));

        Selected = Find(_key) ?? Find(_key is { } k ? (k.Id, !k.Ghost) : null) ?? Rows.FirstOrDefault(r => !r.IsGhost) ?? Rows.FirstOrDefault();
        RaiseBar();
    }

    EntryRowViewModel? Find((int Id, bool Ghost)? key) =>
        key is { } k ? Rows.FirstOrDefault(r => r.Entry.Id == k.Id && r.IsGhost == k.Ghost) : null;

    void RaiseBar()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasValueFixes));
        OnPropertyChanged(nameof(HasStaged));
        OnPropertyChanged(nameof(PendingSummary));
        OnPropertyChanged(nameof(RatingChanges));
        OnPropertyChanged(nameof(HasRatingChanges));
        OnPropertyChanged(nameof(CanStageRecommended));
        OnPropertyChanged(nameof(StageRecommendedLabel));
        DiscardCommand.NotifyCanExecuteChanged();
        ReviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChanged(EntryRowViewModel? oldValue, EntryRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            _key = (newValue.Entry.Id, newValue.IsGhost);
        }
        Panel = newValue is null || Pending.Result is null ? null : new EntryPanelViewModel(newValue, Pending.Result, Navigator);
    }

    /// <summary>Arrive from the Dashboard: pick out the line a scanned entry became.</summary>
    public bool Select(PathScope scope, int index)
    {
        if (Pending.IdOf(scope, index) is not { } id) return false;
        var row = Rows.FirstOrDefault(r => r.Entry.Id == id && !r.IsGhost) ?? Rows.FirstOrDefault(r => r.Entry.Id == id);
        if (row is null) return false;
        Selected = row;
        return true;
    }

    internal void Select(EntryRowViewModel row) => Selected = row;

    [RelayCommand]
    private void StageRecommended() => Pending.StageRecommended();

    [RelayCommand(CanExecute = nameof(HasStaged))]
    private void Discard() => Pending.Discard();

    [RelayCommand(CanExecute = nameof(HasStaged))]
    private void Review() => Navigator.ToReview();
}

/// <summary>
/// One line of a PATH as it would be: its text, what's there, and whether something staged changes it. A
/// <see cref="IsGhost"/> line is one that's gone from this PATH, shown where it was. Its commands are the editing
/// actions the side panel and the right-click menu offer.
/// </summary>
public sealed partial class EntryRowViewModel : ViewModelBase
{
    readonly EntriesViewModel _owner;
    readonly int _count;

    /// <param name="position">Its place in the PATH as it would be, or -1 for a line that's gone.</param>
    /// <param name="moved">It was moved within this PATH, rather than only shifted by what moved around it.</param>
    public EntryRowViewModel(DraftEntry entry, PathScope scope, int position, int count, EntriesViewModel owner, bool moved)
    {
        Entry = entry;
        Scope = scope;
        Position = position;
        _count = count;
        _owner = owner;
        _editText = entry.Text;
        IsGhost = position < 0;

        var pending = owner.Pending;
        var result = pending.Result!;
        Scanned = entry.Origin is { } o ? result.Snapshot.EntriesIn(o.Scope).FirstOrDefault(x => x.Index == o.Index) : null;

        var expanded = pending.Expand(entry.Text);
        Expanded = string.Equals(expanded, entry.Text, StringComparison.Ordinal) ? "" : expanded;

        Fixes = pending.FixesFor(entry.Id);
        var worst = Scanned is null ? null : result.GroupsFor(Scanned).Where(g => g.Severity > Severity.Info).Select(g => (Severity?)g.Severity).Max();
        HasProblem = worst is not null;
        ProblemBrush = HasProblem ? Severities.BrushFor(worst) : Brushes.Transparent;

        var draft = pending.Draft;
        var original = pending.Base.Find(entry.Id);
        var wasAt = pending.Base.Locate(entry.Id);
        var staged = Fixes.FirstOrDefault(f => f.IsStaged);
        // What it says in the list, and in the side panel after "Staged: ".
        (Change, var said) = original switch
        {
            _ when IsGhost => draft.Locate(entry.Id) is { } to
                ? Twice($"moves to the {EntryWords.ScopeName(to.Scope)} PATH")
                : Twice("removed"),
            null => Twice("added"),
            _ when wasAt is { } w && w.Scope != scope => Twice($"moved here from the {EntryWords.ScopeName(w.Scope)} PATH"),
            _ when original.Text != entry.Text => Twice($"was {EntryWords.Shown(original.Text)}"),
            _ when moved && wasAt is { } w => Twice($"moved from #{w.Index + 1}"),
            _ when staged is not null => ($"fix staged: {staged.Title}", staged.Title),
            _ => Twice(""),
        };
        ChangeBrush = IsGhost && draft.Locate(entry.Id) is null ? Brush("DangerBrush") : Brush("AccentBrush");
        StagedLine = said.Length == 0 ? "" : "Staged: " + said;

        static (string, string) Twice(string text) => (text, text);

        (Status, StatusBrush) = Scanned is null ? ("", Brush("MutedBrush")) : StatusOf(Scanned, result.Context.Resolve(Scanned));
    }

    public DraftEntry Entry { get; }
    public PathScope Scope { get; }
    public int Position { get; }
    public bool IsGhost { get; }

    /// <summary>The entry the scan found, or null for one that wasn't there.</summary>
    public PathEntry? Scanned { get; }

    public string Number => IsGhost ? "" : $"#{Position + 1}";

    /// <summary>As it will be stored (defects are picked out in the view).</summary>
    public string Text => Entry.Text;

    /// <summary>What it expands to, or "" when that's the same.</summary>
    public string Expanded { get; }
    public bool HasExpanded => Expanded.Length > 0;

    /// <summary>What's there, in a word or two ("missing", "junction"): blank for an ordinary folder.</summary>
    public string Status { get; }
    public IBrush StatusBrush { get; }
    public bool HasStatus => Status.Length > 0;

    /// <summary>What something staged does to it ("removed", "moved from #3"), or "".</summary>
    public string Change { get; }
    public IBrush ChangeBrush { get; }
    public bool HasChange => Change.Length > 0;

    /// <summary>The same, as the side panel says it: "Staged: Lock down C:\Tools".</summary>
    public string StagedLine { get; }

    public bool HasProblem { get; }
    public IBrush ProblemBrush { get; }

    /// <summary>The fixes that change this line.</summary>
    public IReadOnlyList<FixRowViewModel> Fixes { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText;

    public string MoveLabel => Scope == PathScope.Machine ? "Move to your user PATH" : "Move to the system PATH";
    public string MoveTip => Scope == PathScope.Machine
        ? "It goes to the front of your user PATH, so only you get it. Your user PATH is written first, then it's taken out of the system PATH (one UAC prompt)."
        : "It goes to the end of the system PATH, so every account gets it (needs admin).";

    internal PendingChanges Pending => _owner.Pending;

    [RelayCommand]
    private void Open() => _owner.Select(this);

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Pending.Edit(new MoveEntry(Entry.Id, Scope, Position - 1));

    bool CanMoveUp() => !IsGhost && Position > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Pending.Edit(new MoveEntry(Entry.Id, Scope, Position + 1));

    bool CanMoveDown() => !IsGhost && Position < _count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveScope))]
    private void MoveScope() => Pending.MoveToOtherScope(Entry.Id);

    bool CanMoveScope() => !IsGhost && Entry.Text.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(IsLive))]
    private void Delete() => Pending.Edit(new RemoveEntry(Entry.Id));

    bool IsLive() => !IsGhost;

    [RelayCommand(CanExecute = nameof(IsLive))]
    private void BeginEdit()
    {
        _owner.Select(this);
        EditText = Entry.Text;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveEdit()
    {
        IsEditing = false;
        if (EditText != Entry.Text) Pending.Edit(new ReplaceText(Entry.Id, EditText));
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    /// <summary>You took it out (or moved it away) by hand, so it can be put back.</summary>
    public bool CanPutBack => IsGhost && Pending.CanPutBack(Entry.Id, Scope);

    [RelayCommand(CanExecute = nameof(CanPutBack))]
    private void PutBack() => Pending.PutBack(Entry.Id, Scope);

    /// <summary>You moved, reordered or edited it yourself, so that can be undone.</summary>
    public bool CanRevert => !IsGhost && Pending.HasEditsTo(Entry.Id);

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert() => Pending.Revert(Entry.Id);

    /// <summary>Stage every fix for this line.</summary>
    public bool CanStageFixes => Fixes.Any(f => !f.IsStaged);

    [RelayCommand(CanExecute = nameof(CanStageFixes))]
    private void StageFixes()
    {
        foreach (var fix in Fixes.Where(f => !f.IsStaged).ToList()) fix.IsStaged = true;
    }

    /// <summary>What's at the entry, in a word or two: blank when it's an ordinary, existing folder.</summary>
    internal static (string, IBrush) StatusOf(PathEntry entry, ResolvedEntry resolved)
    {
        if (entry.Form == PathForm.Empty) return ("empty", Severities.BrushFor(Severity.Medium));
        if (entry.UnresolvedVariables.Count > 0)
            return ($"%{entry.UnresolvedVariables[0]}% isn't expanded", Severities.BrushFor(Severity.High));
        if (entry.ProbePath is null) return ("relative", Severities.BrushFor(Severity.High));
        var own = resolved.Own;
        if (own is null) return ("not looked at", Brush("MutedBrush"));
        if (own.Status == ProbeStatus.SkippedNetwork) return ("network: left alone", Severities.BrushFor(Severity.Medium));
        if (own.Status == ProbeStatus.Failed) return ("couldn't read", Brush("DangerBrush"));
        if (own.Drive == DriveKind.NoRootDir) return ("drive not mounted", Severities.BrushFor(Severity.Low));
        if (!own.Exists) return ("missing", Severities.BrushFor(Severity.Low));
        if (!own.IsDirectory) return ("a file, not a folder", Severities.BrushFor(Severity.Low));
        if (resolved.ViaLink) return (own.IsJunction ? "junction" : "symlink", Brush("AccentBrush"));
        if (own.Drive is DriveKind.MappedNetwork or DriveKind.Unc) return ("network", Severities.BrushFor(Severity.Medium));
        if (own.Drive == DriveKind.Removable) return ("removable drive", Severities.BrushFor(Severity.Medium));
        return ("", Brush("MutedBrush"));
    }
}

/// <summary>
/// The side panel for the picked-out line: what's wrong with it and what can be done about each problem, the
/// actions you can take yourself, and (folded away) everything the scan captured about it.
/// </summary>
public sealed class EntryPanelViewModel
{
    public EntryPanelViewModel(EntryRowViewModel row, ScanResult result, INavigator navigator)
    {
        Row = row;
        var scope = row.Scope == PathScope.Machine ? "System" : "User";
        Heading = row.IsGhost ? $"Was in the {scope.ToLowerInvariant()} PATH" : $"{scope} PATH #{row.Position + 1}";
        Status = row.HasStatus ? row.Status : row.Scanned is null ? "" : "an existing folder";

        var issues = new List<IssueViewModel>();
        var offered = new HashSet<FixRowViewModel>();
        if (row.Scanned is { } scanned)
        {
            foreach (var group in result.GroupsFor(scanned))
            {
                var fixes = row.Pending.FixesForProblem(group);
                offered.UnionWith(fixes);
                issues.Add(new IssueViewModel(group, fixes, navigator));
            }
        }
        Issues = issues.Where(i => !i.IsNote).ToList();
        Notes = issues.Where(i => i.IsNote).ToList();
        OtherFixes = row.Fixes.Where(f => !offered.Contains(f)).ToList();
        NoProblemsLine = row.Scanned is null || Issues.Count > 0 ? "" : "Nothing wrong with this one.";

        if (row.Scanned is { } entry) (Facts, Access, AccessNote) = EntryFacts.Of(entry, result);
        else (Facts, Access, AccessNote) = ([], [], "It isn't in the scan, so there's nothing captured about it.");
    }

    /// <summary>The line itself, whose commands the panel's buttons run.</summary>
    public EntryRowViewModel Row { get; }

    public string Heading { get; }
    public string Status { get; }
    public bool HasStatus => Status.Length > 0;

    public IReadOnlyList<IssueViewModel> Issues { get; }
    public bool HasIssues => Issues.Count > 0;
    public IReadOnlyList<IssueViewModel> Notes { get; }
    public bool HasNotes => Notes.Count > 0;

    /// <summary>Fixes that change this line for a problem it isn't itself listed under (a shared lock-down).</summary>
    public IReadOnlyList<FixRowViewModel> OtherFixes { get; }
    public bool HasOtherFixes => OtherFixes.Count > 0;

    public string NoProblemsLine { get; }
    public bool HasNoProblemsLine => NoProblemsLine.Length > 0;

    public IReadOnlyList<InfoRowViewModel> Facts { get; }
    public IReadOnlyList<AccessRowViewModel> Access { get; }
    public bool HasAccess => Access.Count > 0;
    public string AccessNote { get; }
    public bool HasAccessNote => AccessNote.Length > 0;
}

/// <summary>One problem with an entry: what it is, why it matters, and the fix to stage (or what to do by hand).</summary>
public sealed partial class IssueViewModel(FindingGroup group, IReadOnlyList<FixRowViewModel> fixes, INavigator navigator) : ViewModelBase
{
    public FindingGroup Group { get; } = group;
    public string Title => CharWrapTextBlock.BreakAfterSeparators(Group.Primary.Title);
    public IBrush SeverityBrush => Severities.BrushFor(Group.Severity);
    public string SeverityWord => Severities.Word(Group.Severity).ToUpperInvariant();
    public bool IsNote => Group.Severity == Severity.Info;

    public string Why => Group.Primary.Why;

    public IReadOnlyList<FixRowViewModel> Fixes { get; } = fixes;
    public bool HasFixes => Fixes.Count > 0;

    /// <summary>What to do about it by hand, when there's nothing to stage.</summary>
    public string Advice => HasFixes ? "" : Group.Primary.Fix;
    public bool HasAdvice => Advice.Length > 0;

    public string LearnLabel => Group.Primary.Learn is { } topic && LearnLibrary.Find(topic) is { } article ? $"Learn: {article.Title}" : "";
    public bool HasLearn => LearnLabel.Length > 0;

    [RelayCommand]
    private void OpenLearn()
    {
        if (Group.Primary.Learn is { } topic) navigator.ToLearn(topic);
    }
}

/// <summary>Everything captured about one entry, for the panel's folded-away details.</summary>
internal static class EntryFacts
{
    public static (IReadOnlyList<InfoRowViewModel> Facts, IReadOnlyList<AccessRowViewModel> Access, string AccessNote) Of(PathEntry entry, ScanResult result)
    {
        var snapshot = result.Snapshot;
        var resolved = result.Context.Resolve(entry);
        var own = resolved.Own;
        var final = resolved.Final;

        var facts = new List<InfoRowViewModel>
        {
            new("Stored as", snapshot.PathFor(entry.Scope).Kind switch
            {
                PathValueKind.ExpandString => "REG_EXPAND_SZ: %VARIABLES% are expanded",
                PathValueKind.String => "REG_SZ: taken literally, nothing is expanded",
                PathValueKind.Other => "not a string value, so Windows ignores it",
                _ => "not set",
            }),
            new("Form", FormText(entry.Form)),
        };
        if (entry.Variables.Count > 0) facts.Add(new("Variables", string.Join(", ", entry.Variables.Select(v => $"%{v}%"))));
        if (entry.UnresolvedVariables.Count > 0) facts.Add(new("Left unexpanded", string.Join(", ", entry.UnresolvedVariables.Select(v => $"%{v}%"))));
        if (DefectsText(entry.Defects) is { Length: > 0 } defects) facts.Add(new("Text defects", defects));
        if (own is not null)
        {
            facts.Add(new("Drive", DriveText(own)));
            if (own.LongPath is { } longPath) facts.Add(new("Long name", longPath));
            if (own.Note is { } note) facts.Add(new("Note", note));
            if (!own.Exists && own.NearestExistingAncestor is { } ancestor) facts.Add(new("Nearest folder that exists", ancestor));
        }
        foreach (var link in resolved.Links)
            facts.Add(new(link.IsJunction ? "Junction to" : "Symlink to",
                (link.ReparseTarget ?? "?") + (link.ReparseTargetIsNetwork ? " (on the network: left alone)" : "")));
        if (final is { OwnerSid: { } owner })
            facts.Add(new("Owner", string.Equals(owner, snapshot.Host.UserSid, StringComparison.OrdinalIgnoreCase) ? "you" : WellKnownSids.NameOf(owner)));
        if (final?.SecurityError is { } error) facts.Add(new("Permissions", $"couldn't be read: {error}"));
        if (final?.CommandFiles is { } files) facts.Add(new("Commands", files.Count == 0 ? "none" : $"{files.Count}: " + string.Join(", ", files.Take(12)) + (files.Count > 12 ? ", …" : "")));

        IReadOnlyList<AccessRowViewModel> access = final is { Exists: true, Access.Count: > 0 }
            ? Perspectives.Select(p => new AccessRowViewModel(p, final.AccessFor(p), snapshot.Host.UserSid)).ToList()
            : [];
        var accessNote = final is null ? "This entry wasn't looked at, so who can write it isn't known."
            : !final.Exists ? "The folder doesn't exist. Whether someone could create it is what matters: see the problems above."
            : final.Access.Count == 0 ? "Its permissions couldn't be read."
            : resolved.ViaLink ? $"Judged at the link's target, {final.Path}, where files really land." : "";
        return (facts, access, accessNote);
    }

    /// <summary>The perspectives, in the order the details list them.</summary>
    static readonly Perspective[] Perspectives =
        [Perspective.CurrentUserUnelevated, Perspective.CurrentUserElevated, Perspective.System, Perspective.StandardUser];

    static string FormText(PathForm form) => form switch
    {
        PathForm.Absolute => "a full path",
        PathForm.DriveRelative => "drive-relative (C:folder): depends on that drive's current folder",
        PathForm.RootRelative => @"root-relative (\folder): depends on the current drive",
        PathForm.Relative => "relative: depends on the current folder",
        PathForm.Unc => @"a network share (\\server\share)",
        PathForm.DevicePath => "a device path",
        _ => "empty",
    };

    static string DriveText(DirectoryFacts facts) => facts.Drive switch
    {
        DriveKind.Fixed => facts.DriveTarget is { } t ? $"local (a subst drive for {t})" : "local",
        DriveKind.Removable => "removable",
        DriveKind.CdRom => "CD/DVD",
        DriveKind.RamDisk => "RAM disk",
        DriveKind.MappedNetwork => $"a mapped network drive{(facts.DriveTarget is { } t ? $" ({t})" : "")}",
        DriveKind.Unc => "a network share",
        DriveKind.NoRootDir => "not mounted",
        _ => "unknown",
    };

    static string DefectsText(HygieneDefects d)
    {
        var words = new List<string>();
        if (d.HasFlag(HygieneDefects.Quotes)) words.Add("quotes");
        if (d.HasFlag(HygieneDefects.LeadingWhitespace)) words.Add("leading spaces");
        if (d.HasFlag(HygieneDefects.TrailingWhitespace)) words.Add("trailing spaces");
        if (d.HasFlag(HygieneDefects.DoubledBackslash)) words.Add("doubled backslashes");
        if (d.HasFlag(HygieneDefects.ForwardSlash)) words.Add("forward slashes");
        if (d.HasFlag(HygieneDefects.TrailingBackslash)) words.Add("a trailing backslash");
        return string.Join(", ", words);
    }
}

/// <summary>What one perspective can do to the folder, and the ACEs that let it.</summary>
public sealed class AccessRowViewModel
{
    public AccessRowViewModel(Perspective perspective, AccessResult? access, string userSid)
    {
        Who = Severities.PerspectiveName(perspective);
        if (access is null || !access.Evaluated)
        {
            Can = access?.Error is { } e ? $"couldn't be checked: {e}" : "not checked";
            Brush = Theme.Brush("MutedBrush");
            Through = "";
            return;
        }

        var rights = new List<string>();
        if (access.CanAddFiles) rights.Add("add files");
        if (access.CanAddSubdirectories) rights.Add("create folders");
        if (access.CanWriteDac) rights.Add(access.WriteDacFromOwnership ? "change permissions (as owner)" : "change permissions");
        if (access.CanWriteOwner) rights.Add("take ownership");
        Can = rights.Count == 0 ? "read only" : string.Join(", ", rights);
        Brush = Writability.CanPlantFiles(access) ? Theme.Brush("WarnBrush") : Theme.Brush("FgBrush");
        Through = string.Join("; ", access.GrantedBy.Select(a => Writability.Describe(a, userSid)));
    }

    public string Who { get; }
    public string Can { get; }
    public IBrush Brush { get; }
    public string Through { get; }
    public bool HasThrough => Through.Length > 0;
}
