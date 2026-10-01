using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Controls;
using Pathology.App.Scanning;
using Pathology.App.Theming;
using Pathology.Core.Detection;
using Pathology.Core.Detection.Detectors;
using Pathology.Core.Health;
using Pathology.Core.Model;

namespace Pathology.App.ViewModels;

/// <summary>
/// The landing page: Security, Correctness and Hygiene side by side, each rated by its worst problem, with what
/// holds the rating and its worst few problems. Below them, the notes, UAC exposure, length headroom and what the
/// scan looked at.
/// </summary>
public sealed partial class HealthViewModel : ScanPageViewModel
{
    public HealthViewModel(ScanSession session, INavigator navigator) : base(session, navigator) => Rebuild(session.Current);

    public override string Title => "Health";

    [ObservableProperty] private IReadOnlyList<CategoryCardViewModel> _cards = [];

    [ObservableProperty] private bool _isClean;

    [ObservableProperty] private string _notesLine = "";
    [ObservableProperty] private bool _hasNotes;

    [ObservableProperty] private string _uacHeadline = "";
    [ObservableProperty] private string _uacDetail = "";
    [ObservableProperty] private IBrush _uacBrush = Severities.BrushFor(null);
    [ObservableProperty] private IReadOnlyList<string> _uacFolders = [];
    [ObservableProperty] private FindingGroup? _uacGroup;

    [ObservableProperty] private IReadOnlyList<HeadroomBarViewModel> _headroom = [];
    [ObservableProperty] private string _storedLengths = "";

    [ObservableProperty] private IReadOnlyList<InfoRowViewModel> _scanInfo = [];

    protected override void Rebuild(ScanResult? result)
    {
        if (result is null)
        {
            Cards = [];
            HasNotes = false;
            return;
        }

        var health = result.Health;
        Cards = health.Categories.Select(c => new CategoryCardViewModel(c, Navigator)).ToList();
        IsClean = health.IsClean;

        HasNotes = health.Notes.Count > 0;
        NotesLine = health.Notes.Count == 1
            ? "1 note: worth knowing, but not a problem"
            : $"{health.Notes.Count} notes: worth knowing (which tool wins, a stale Explorer PATH), but not problems";

        BuildUac(result);
        BuildHeadroom(result.Snapshot);
        BuildScanInfo(result);
    }

    void BuildUac(ScanResult result)
    {
        var group = result.Diagnosis.Groups.FirstOrDefault(g => g.Members.Any(f => f.Rule == "SEC-07"));
        var finding = group?.Members.First(f => f.Rule == "SEC-07");
        UacGroup = group;
        if (!result.Context.HasSplitToken || !result.Context.UserIsAdmin)
        {
            UacHeadline = "Not applicable";
            UacDetail = result.Context.UserIsAdmin
                ? "UAC isn't splitting your token, so there are no separate elevated sessions to expose."
                : "You're a standard user, so there are no elevated sessions of yours for PATH to leak into.";
            UacBrush = Severities.BrushFor(null);
            UacFolders = [];
        }
        else if (finding is null)
        {
            UacHeadline = "Nothing exposed";
            UacDetail = "No folder you can write without elevating is searched by the programs you run as administrator.";
            UacBrush = Severities.BrushFor(null);
            UacFolders = [];
        }
        else
        {
            UacHeadline = finding.Severity == Severity.Info ? "Only the Windows baseline" : Severities.RatingWord(finding.Severity);
            UacDetail = finding.Title;
            UacBrush = Severities.BrushFor(finding.Severity);
            UacFolders = finding.Entries.Select(e => e.Display).ToList();
        }
    }

    void BuildHeadroom(PathSnapshot snapshot)
    {
        var effective = LengthHeadroom.EffectivePath(snapshot).Length;
        Headroom =
        [
            new("Older tools and the classic Environment Variables dialog", effective, LengthHeadroom.LegacyLimit),
            new("Any environment variable", effective, LengthHeadroom.VariableLimit),
        ];
        StoredLengths = $"A new process's PATH is {N(effective)} characters. Stored: machine {N(snapshot.MachinePath.Length)}, " +
                        $"user {N(snapshot.UserPath.Length)}.";
    }

    void BuildScanInfo(ScanResult result)
    {
        var s = result.Snapshot;
        var dirs = s.Directories;
        var skipped = dirs.Count(d => d.Status == ProbeStatus.SkippedNetwork);
        var perspectives = s.Perspectives
            .Select(p => Severities.PerspectivePhrase(p.Perspective) + (p.Note is { } note ? $" ({note})" : ""))
            .ToList();

        ScanInfo =
        [
            new("Scanned", s.CapturedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)),
            new("Entries", $"{s.EntriesIn(PathScope.Machine).Count()} machine, {s.EntriesIn(PathScope.User).Count()} user"),
            new("Folders looked at", $"{dirs.Count} ({dirs.Count(d => d.Exists)} exist)"),
            new("Commands on PATH", N(result.Diagnosis.Shadows.All.Count)),
            new("Judged as", string.Join(", ", perspectives)),
            new("Network paths", s.Options.ProbeNetworkPaths
                ? "probed (you opted in)"
                : skipped == 0 ? "none to probe" : $"{skipped} left alone, judged from their text (see Settings)"),
        ];
    }

    static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void OpenNotes() => Navigator.ToFindings(new FindingsQuery(NotesOnly: true));

    [RelayCommand]
    private void OpenUac()
    {
        if (UacGroup is { } g) Navigator.ToFindings(new FindingsQuery(RootCause: g.RootCause));
    }
}

/// <summary>One category's card on the Health page.</summary>
public sealed partial class CategoryCardViewModel : ViewModelBase
{
    readonly CategoryHealth _health;
    readonly INavigator _navigator;

    public CategoryCardViewModel(CategoryHealth health, INavigator navigator)
    {
        _health = health;
        _navigator = navigator;
        Problems = health.Problems.Take(3).Select(p => new ProblemRowViewModel(p, navigator)).ToList();
    }

    public string Name => _health.Category.ToString();
    public string Heading => Name.ToUpperInvariant();
    public string RatingWord => Severities.RatingWord(_health.Rating);
    public IBrush RatingBrush => Severities.BrushFor(_health.Rating);
    public bool IsClean => _health.IsClean;

    /// <summary>"9 high · 2 medium", or "Nothing to fix".</summary>
    public string CountsLine => IsClean ? "Nothing to fix" : Severities.Counts(_health.Count);

    /// <summary>"Fix 9 to bring it to Medium", "Fix 1 to make it clean".</summary>
    public string HintLine
    {
        get
        {
            if (IsClean) return "";
            var n = _health.Holding.Count();
            return _health.AfterHolding is { } after
                ? $"Fix {n} to bring it to {Capitalise(Severities.Word(after))}"
                : $"Fix {n} to make it clean";
        }
    }

    public IReadOnlyList<ProblemRowViewModel> Problems { get; }

    public bool HasMore => _health.Problems.Count > Problems.Count;
    public string AllLabel => _health.Problems.Count switch
    {
        0 => "",
        1 => $"See the {Name.ToLowerInvariant()} problem",
        var n => $"All {n} {Name.ToLowerInvariant()} problems",
    };

    [RelayCommand]
    private void Open() => _navigator.ToFindings(new FindingsQuery(Category: _health.Category));

    static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>A problem as one line: severity dot, title, scope.</summary>
public sealed partial class ProblemRowViewModel(FindingGroup group, INavigator navigator) : ViewModelBase
{
    public FindingGroup Group { get; } = group;
    public string Title => CharWrapTextBlock.BreakAfterSeparators(Group.Primary.Title);
    public IBrush SeverityBrush => Severities.BrushFor(Group.Severity);
    public string ScopeText => Severities.ScopeWord(Group.Primary.Scope);

    [RelayCommand]
    private void Open() => navigator.ToFindings(new FindingsQuery(RootCause: Group.RootCause));
}

/// <summary>One length limit, as a bar.</summary>
public sealed class HeadroomBarViewModel(string label, int used, int limit) : ViewModelBase
{
    public string Label { get; } = label;
    public double Fraction { get; } = Math.Min(1.0, used / (double)limit);

    public string Text => $"{used.ToString("N0", CultureInfo.InvariantCulture)} of {limit.ToString("N0", CultureInfo.InvariantCulture)} ({(int)(used * 100.0 / limit)}%)";

    /// <summary>Coloured as COR-08 rates it: medium from 80%, high from 95%.</summary>
    public IBrush Brush => Severities.BrushFor(Fraction >= 0.95 ? Severity.High : Fraction >= 0.8 ? Severity.Medium : null);
}

public sealed record InfoRowViewModel(string Label, string Value);
