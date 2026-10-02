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
using Pathology.Core.Learn;
using Pathology.Core.Model;

namespace Pathology.App.ViewModels;

/// <summary>
/// The landing page, kept short: Security, Correctness and Hygiene, each rated by its worst problem; the problems
/// PATHology can fix, worst first; and the ones it can only explain. Everything links to the entry it's about,
/// where it's fixed.
/// </summary>
public sealed partial class DashboardViewModel : ScanPageViewModel
{
    readonly PendingChanges _pending;

    public DashboardViewModel(ScanSession session, INavigator navigator, PendingChanges pending) : base(session, navigator)
    {
        _pending = pending;
        pending.Changed += (_, _) => Refresh();
        Refresh();
    }

    public override string Title => "Dashboard";

    [ObservableProperty] private IReadOnlyList<CategoryCardViewModel> _cards = [];
    [ObservableProperty] private bool _isClean;

    /// <summary>Problems with a fix to stage, worst first.</summary>
    [ObservableProperty] private IReadOnlyList<DashboardItemViewModel> _toFix = [];

    /// <summary>Problems (and notes about the PATH as a whole) with no fix to stage: worth knowing, fixed by hand.</summary>
    [ObservableProperty] private IReadOnlyList<DashboardItemViewModel> _worthKnowing = [];

    [ObservableProperty] private string _toFixLine = "";
    [ObservableProperty] private string _summaryLine = "";
    [ObservableProperty] private IReadOnlyList<InfoRowViewModel> _scanInfo = [];

    public bool HasToFix => ToFix.Count > 0;
    public bool HasWorthKnowing => WorthKnowing.Count > 0;

    /// <summary>Scan results arrive through <see cref="PendingChanges"/>, which re-plans first and then tells every page.</summary>
    protected override void Rebuild(ScanResult? result) { }

    void Refresh()
    {
        var result = _pending.Result;
        if (result is null)
        {
            Cards = [];
            ToFix = [];
            WorthKnowing = [];
            IsClean = false;
            Raise();
            return;
        }

        var health = result.Health;
        Cards = health.Categories.Select(c => new CategoryCardViewModel(c)).ToList();
        IsClean = health.IsClean;

        // One row per problem, worst first: those with a fix to stage, then the rest. Notes about one entry show on
        // that entry; notes about the PATH as a whole show here.
        var toFix = new List<DashboardItemViewModel>();
        var worthKnowing = new List<DashboardItemViewModel>();
        var offered = new HashSet<FixRowViewModel>();
        foreach (var group in result.Diagnosis.Groups)
        {
            var fix = _pending.FixesForProblem(group).FirstOrDefault();
            if (fix is not null)
            {
                offered.Add(fix);
                toFix.Add(new DashboardItemViewModel(group.Primary.Title, group.Severity, group, fix, _pending.ScopeOfFix(fix.Fix), Navigator));
            }
            else if (group.Severity > Severity.Info || !group.Members.Any(f => f.Entries.Count > 0))
                worthKnowing.Add(new DashboardItemViewModel(group.Primary.Title, group.Severity, group, null, group.Primary.Scope ?? PathScope.Machine, Navigator));
        }
        // Fixes for no one problem (putting Windows' folders first).
        foreach (var fix in _pending.Fixes.Where(f => !offered.Contains(f) && f.Fix.RootCauses.Count == 0))
            toFix.Add(new DashboardItemViewModel(fix.Problem, fix.Fix.Severity, null, fix, _pending.ScopeOfFix(fix.Fix), Navigator));

        ToFix = toFix.OrderByDescending(i => i.Severity).ToList();
        WorthKnowing = worthKnowing;
        ToFixLine = toFix.Count == 0 ? ""
            : $"{PendingChanges.Words(toFix.Count, "problem has a fix", "problems have a fix")} PATHology can make for you. Open one to see it on its entry, and stage it there.";

        BuildSummary(result);
        Raise();
    }

    void Raise()
    {
        OnPropertyChanged(nameof(HasToFix));
        OnPropertyChanged(nameof(HasWorthKnowing));
    }

    void BuildSummary(ScanResult result)
    {
        var s = result.Snapshot;
        var effective = LengthHeadroom.EffectivePath(s).Length;
        SummaryLine = $"Scanned {s.CapturedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)} · " +
                      $"{PendingChanges.Words(s.EntriesIn(PathScope.Machine).Count(), "system entry", "system entries")} and " +
                      $"{s.EntriesIn(PathScope.User).Count()} user · " +
                      $"a new program's PATH is {N(effective)} of the {N(LengthHeadroom.LegacyLimit)} characters older tools can take";

        var dirs = s.Directories;
        var skipped = dirs.Count(d => d.Status == ProbeStatus.SkippedNetwork);
        var perspectives = s.Perspectives
            .Select(p => Severities.PerspectivePhrase(p.Perspective) + (p.Note is { } note ? $" ({note})" : ""))
            .ToList();
        ScanInfo =
        [
            new("Folders looked at", $"{dirs.Count} ({dirs.Count(d => d.Exists)} exist)"),
            new("Commands on PATH", N(result.Diagnosis.Shadows.All.Count)),
            new("Judged as", string.Join(", ", perspectives)),
            new("Network paths", s.Options.ProbeNetworkPaths
                ? "probed (you opted in)"
                : skipped == 0 ? "none to probe" : $"{skipped} left alone, judged from their text (see Settings)"),
            new("Stored lengths", $"system {N(s.MachinePath.Length)}, user {N(s.UserPath.Length)} characters; any variable can hold {N(LengthHeadroom.VariableLimit)}"),
        ];
    }

    static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>One category's rating on the Dashboard.</summary>
public sealed class CategoryCardViewModel(CategoryHealth health)
{
    public string Name => health.Category.ToString();
    public string Heading => Name.ToUpperInvariant();
    public string RatingWord => Severities.RatingWord(health.Rating);
    public IBrush RatingBrush => Severities.BrushFor(health.Rating);
    public bool IsClean => health.IsClean;

    /// <summary>"9 high · 2 medium", or "Nothing to fix".</summary>
    public string CountsLine => IsClean ? "Nothing to fix" : Severities.Counts(health.Count);

    /// <summary>"Fix 9 to bring it to Medium", "Fix 1 to make it clean".</summary>
    public string HintLine
    {
        get
        {
            if (IsClean) return "";
            var n = health.Holding.Count();
            return health.AfterHolding is { } after
                ? $"Fix {n} to bring it to {Capitalise(Severities.Word(after))}"
                : $"Fix {n} to make it clean";
        }
    }

    static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// A problem as one Dashboard row: severity, title, scope, and the fix when there is one. Opening it goes to the
/// entry it's about (or its PATH, for a problem about the whole value). One with no fix unfolds to say what to do.
/// </summary>
public sealed partial class DashboardItemViewModel : ViewModelBase
{
    readonly INavigator _navigator;
    readonly PathScope _scope;
    readonly EntryRef? _entry;

    public DashboardItemViewModel(string title, Severity severity, FindingGroup? group, FixRowViewModel? fix, PathScope scope, INavigator navigator)
    {
        _navigator = navigator;
        Group = group;
        Fix = fix;
        Severity = severity;
        Title = CharWrapTextBlock.BreakAfterSeparators(title);
        SeverityBrush = Severities.BrushFor(severity);
        _entry = group?.Members.SelectMany(f => f.Entries).FirstOrDefault();
        _scope = _entry?.Scope ?? scope;
        ScopeText = group is { Primary.Scope: null } && _entry is null ? "both" : EntryWords.ScopeName(_scope);
    }

    public FindingGroup? Group { get; }
    public FixRowViewModel? Fix { get; }
    public Severity Severity { get; }

    public string Title { get; }
    public IBrush SeverityBrush { get; }
    public string ScopeText { get; }

    /// <summary>"Fix: Lock down C:\Tools", for a row with one.</summary>
    public string FixLine => Fix is { } f ? $"Fix: {f.Title}" + (f.Fix.NeedsAdmin ? " (needs admin)" : "") : "";
    public bool HasFix => Fix is not null;
    public bool IsStaged => Fix?.IsStaged == true;

    /// <summary>Where to send you: the entry, or the PATH as a whole.</summary>
    public string OpenLabel => _entry is not null ? "Show the entry" : $"Show the {ScopeText} PATH";
    public bool CanOpen => HasFix || _entry is not null;

    [ObservableProperty] private bool _isExpanded;

    public string Why => Group?.Primary.Why ?? Fix?.Problem ?? "";
    public string Advice => Group?.Primary.Fix ?? "";
    public bool HasAdvice => !HasFix && Advice.Length > 0;

    public string LearnLabel => Group?.Primary.Learn is { } topic && LearnLibrary.Find(topic) is { } article ? $"Learn: {article.Title}" : "";
    public bool HasLearn => LearnLabel.Length > 0;

    /// <summary>A row with a fix opens its entry; one without unfolds to say what to do.</summary>
    [RelayCommand]
    private void Activate()
    {
        if (HasFix) Open();
        else IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    private void Open()
    {
        if (_entry is { } e) _navigator.ToEntry(e.Scope, e.Index);
        else _navigator.ToEntries(_scope);
    }

    [RelayCommand]
    private void OpenLearn()
    {
        if (Group?.Primary.Learn is { } topic) _navigator.ToLearn(topic);
    }
}

public sealed record InfoRowViewModel(string Label, string Value);
