using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Scanning;
using Pathology.App.Theming;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using static Pathology.App.Theme;

namespace Pathology.App.ViewModels;

/// <summary>
/// The machine and user PATH, in the order Windows searches them: each entry's text, what's there, who can write
/// it from each perspective, and the findings about it. The detail pane has everything the scan captured.
/// </summary>
public sealed partial class EntriesViewModel : ScanPageViewModel
{
    public EntriesViewModel(ScanSession session, INavigator navigator) : base(session, navigator) => Rebuild(session.Current);

    public override string Title => "Entries";

    /// <summary>The perspectives, in matrix column order.</summary>
    public static IReadOnlyList<Perspective> Columns { get; } =
        [Perspective.CurrentUserUnelevated, Perspective.CurrentUserElevated, Perspective.System, Perspective.StandardUser];

    public IReadOnlyList<string> ColumnHeaders { get; } = Columns.Select(Severities.PerspectiveShort).ToList();

    [ObservableProperty] private IReadOnlyList<EntrySectionViewModel> _sections = [];

    /// <summary>Show each entry expanded (true) or exactly as stored (false).</summary>
    [ObservableProperty] private bool _showExpanded = true;

    /// <summary>Pick out quotes, stray spaces, doubled and forward slashes in the text.</summary>
    [ObservableProperty] private bool _showDefects = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private EntryRowViewModel? _selected;

    [ObservableProperty] private EntryDetailViewModel? _detail;

    public bool HasSelection => Selected is not null;

    partial void OnShowExpandedChanged(bool value)
    {
        foreach (var row in Sections.SelectMany(s => s.Rows)) row.ShowExpanded = value;
    }

    partial void OnSelectedChanged(EntryRowViewModel? oldValue, EntryRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        Detail = newValue is null || Result is null ? null : new EntryDetailViewModel(newValue.Entry, Result, Navigator);
    }

    protected override void Rebuild(ScanResult? result)
    {
        var keep = Selected is { } s ? (s.Entry.Scope, s.Entry.Index) : ((PathScope, int)?)null;
        if (result is null)
        {
            Sections = [];
            Selected = null;
            return;
        }
        Sections = [new(PathScope.Machine, result, this), new(PathScope.User, result, this)];
        var rows = Sections.SelectMany(x => x.Rows).ToList();
        Selected = (keep is { } k ? rows.FirstOrDefault(r => r.Entry.Scope == k.Item1 && r.Entry.Index == k.Item2) : null)
                   ?? rows.FirstOrDefault();
    }

    /// <summary>Arrive from a finding: select that entry.</summary>
    public void Select(PathScope scope, int index)
    {
        if (Sections.SelectMany(s => s.Rows).FirstOrDefault(r => r.Entry.Scope == scope && r.Entry.Index == index) is { } row)
            Selected = row;
    }

    internal void Select(EntryRowViewModel row) => Selected = row;
}

/// <summary>One scope's value: its header (value kind, length) and its entries.</summary>
public sealed class EntrySectionViewModel
{
    public EntrySectionViewModel(PathScope scope, ScanResult result, EntriesViewModel owner)
    {
        var value = result.Snapshot.PathFor(scope);
        Heading = scope == PathScope.Machine ? "MACHINE PATH" : "USER PATH";
        Summary = value.Kind switch
        {
            PathValueKind.Missing => "not set",
            PathValueKind.Other => "not a string value, so Windows ignores it",
            _ => $"{(value.Kind == PathValueKind.ExpandString ? "REG_EXPAND_SZ" : "REG_SZ")} · " +
                 $"{value.Length.ToString("N0", CultureInfo.InvariantCulture)} characters",
        };
        Rows = result.Snapshot.EntriesIn(scope)
            // The trailing ';' Windows writes leaves an empty last entry; it isn't one.
            .Where(e => !e.Defects.HasFlag(HygieneDefects.TrailingSeparator))
            .Select(e => new EntryRowViewModel(e, result, owner))
            .ToList();
    }

    public string Heading { get; }
    public string Summary { get; }
    public IReadOnlyList<EntryRowViewModel> Rows { get; }
    public bool HasRows => Rows.Count > 0;
}

/// <summary>
/// One cell of the who-can-write matrix. A filled square means that perspective can add files. The colour says
/// how much that matters: a standard user (red) is anyone on the PC, you (orange) is anything running as you,
/// and you elevated or SYSTEM (grey) is expected, since administrators can write almost anywhere.
/// </summary>
public sealed record MatrixCellViewModel(Perspective Perspective, bool? Writable)
{
    IBrush Colour => Perspective switch
    {
        Perspective.StandardUser => Severities.BrushFor(Severity.High),
        Perspective.CurrentUserUnelevated => Severities.BrushFor(Severity.Medium),
        _ => Brush("MutedBrush"),
    };

    public IBrush Fill => Writable == true ? Colour : Brushes.Transparent;
    public IBrush Stroke => Writable is null ? Brushes.Transparent : Writable == true ? Colour : Brush("BorderBrush");
    public string Dash => Writable is null ? "–" : "";

    public string Tip => Writable switch
    {
        true => $"{Capitalised} can add files here" + (Perspective is Perspective.System or Perspective.CurrentUserElevated ? " (expected for an administrator)" : ""),
        false => $"{Capitalised} can't add files here",
        _ => "Not known: the folder wasn't looked at, or doesn't exist",
    };

    string Capitalised => Severities.PerspectiveName(Perspective);
}

/// <summary>One entry in the list.</summary>
public sealed partial class EntryRowViewModel : ViewModelBase
{
    readonly EntriesViewModel _owner;

    public EntryRowViewModel(PathEntry entry, ScanResult result, EntriesViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        _showExpanded = owner.ShowExpanded;

        var resolved = result.Context.Resolve(entry);
        var final = resolved.Final;
        (Status, StatusBrush) = StatusOf(entry, resolved);

        var known = final is { Status: ProbeStatus.Probed, Exists: true, IsDirectory: true } && final.Access.Count > 0;
        Matrix = EntriesViewModel.Columns
            .Select(p => new MatrixCellViewModel(p, known ? Writability.CanPlantFiles(final!.AccessFor(p)) : null))
            .ToList();

        var worst = result.GroupsFor(entry).Select(g => (Severity?)g.Severity).Max();
        HasFinding = worst is not null;
        FindingBrush = HasFinding ? Severities.BrushFor(worst) : Brushes.Transparent;
    }

    public PathEntry Entry { get; }

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private bool _showExpanded;

    public string Position => $"#{Entry.Index + 1}";
    public string Text => ShowExpanded ? Entry.Expanded : Entry.Raw;

    public string Status { get; }
    public IBrush StatusBrush { get; }
    public bool HasStatus => Status.Length > 0;

    public IReadOnlyList<MatrixCellViewModel> Matrix { get; }

    public bool HasFinding { get; }
    public IBrush FindingBrush { get; }

    [RelayCommand]
    private void Open() => _owner.Select(this);

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

/// <summary>Everything captured about one entry.</summary>
public sealed partial class EntryDetailViewModel : ViewModelBase
{
    readonly INavigator _navigator;

    public EntryDetailViewModel(PathEntry entry, ScanResult result, INavigator navigator)
    {
        _navigator = navigator;
        var snapshot = result.Snapshot;
        var resolved = result.Context.Resolve(entry);
        var own = resolved.Own;
        var final = resolved.Final;

        Heading = $"{(entry.Scope == PathScope.Machine ? "Machine" : "User")} PATH #{entry.Index + 1}";
        Raw = entry.Raw;
        Expanded = entry.Expanded;
        ExpandedDiffers = !string.Equals(entry.Raw, entry.Expanded, StringComparison.Ordinal);
        (Status, StatusBrush) = EntryRowViewModel.StatusOf(entry, resolved);
        if (Status.Length == 0) Status = "an existing folder";

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
        Facts = facts;

        Access = final is { Exists: true, Access.Count: > 0 }
            ? EntriesViewModel.Columns.Select(p => new AccessRowViewModel(p, final.AccessFor(p), snapshot.Host.UserSid)).ToList()
            : [];
        AccessNote = final is null ? "This entry wasn't looked at, so who can write it isn't known."
            : !final.Exists ? "The folder doesn't exist. Whether someone could create it is what matters: see the findings below."
            : final.Access.Count == 0 ? "Its permissions couldn't be read."
            : resolved.ViaLink ? $"Judged at the link's target, {final.Path}, where files really land." : "";

        Findings = result.GroupsFor(entry).Select(g => new ProblemRowViewModel(g, navigator)).ToList();
    }

    public string Heading { get; }
    public string Raw { get; }
    public string Expanded { get; }
    public bool ExpandedDiffers { get; }
    public string Status { get; }
    public IBrush StatusBrush { get; }

    public IReadOnlyList<InfoRowViewModel> Facts { get; }

    public IReadOnlyList<AccessRowViewModel> Access { get; }
    public bool HasAccess => Access.Count > 0;
    public string AccessNote { get; }
    public bool HasAccessNote => AccessNote.Length > 0;

    public IReadOnlyList<ProblemRowViewModel> Findings { get; }
    public bool HasFindings => Findings.Count > 0;

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
