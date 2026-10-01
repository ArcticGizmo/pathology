using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Controls;
using Pathology.App.Scanning;
using Pathology.App.Theming;
using Pathology.Core.Detection;
using Pathology.Core.Learn;
using Pathology.Core.Model;

namespace Pathology.App.ViewModels;

/// <summary>A filter dropdown's choice: what it says, and what it matches.</summary>
public sealed record FilterOption<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>Which severities the list shows. Problems (Low and up) by default; notes only on request.</summary>
public enum SeverityFilter
{
    Problems,
    High,
    Medium,
    Low,
    Notes,
    Everything,
}

/// <summary>
/// Every problem found, worst first, one row per root cause, with a detail pane that says what it is, why it
/// matters and how to fix it. Filters narrow by severity, category, scope and perspective.
/// </summary>
public sealed partial class FindingsViewModel : ScanPageViewModel
{
    public FindingsViewModel(ScanSession session, INavigator navigator) : base(session, navigator)
    {
        _severity = SeverityOptions[0];
        _category = CategoryOptions[0];
        _scope = ScopeOptions[0];
        _perspective = PerspectiveOptions[0];
        Rebuild(session.Current);
    }

    public override string Title => "Findings";

    public IReadOnlyList<FilterOption<SeverityFilter>> SeverityOptions { get; } =
    [
        new("Problems", SeverityFilter.Problems), new("High", SeverityFilter.High), new("Medium", SeverityFilter.Medium),
        new("Low", SeverityFilter.Low), new("Notes", SeverityFilter.Notes), new("Problems and notes", SeverityFilter.Everything),
    ];

    public IReadOnlyList<FilterOption<FindingCategory?>> CategoryOptions { get; } =
    [
        new("Every category", null), new("Security", FindingCategory.Security),
        new("Correctness", FindingCategory.Correctness), new("Hygiene", FindingCategory.Hygiene),
    ];

    public IReadOnlyList<FilterOption<PathScope?>> ScopeOptions { get; } =
    [
        new("Machine and user", null), new("Machine PATH", PathScope.Machine), new("User PATH", PathScope.User),
    ];

    public IReadOnlyList<FilterOption<Perspective?>> PerspectiveOptions { get; } =
    [
        new("Anyone", null),
        .. Enum.GetValues<Perspective>().Select(p => new FilterOption<Perspective?>(Severities.PerspectiveName(p), p)),
    ];

    [ObservableProperty] private FilterOption<SeverityFilter> _severity;
    [ObservableProperty] private FilterOption<FindingCategory?> _category;
    [ObservableProperty] private FilterOption<PathScope?> _scope;
    [ObservableProperty] private FilterOption<Perspective?> _perspective;

    [ObservableProperty] private IReadOnlyList<FindingRowViewModel> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private FindingRowViewModel? _selected;

    [ObservableProperty] private FindingDetailViewModel? _detail;

    [ObservableProperty] private string _countLine = "";

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => HasResult && Rows.Count == 0;

    partial void OnSeverityChanged(FilterOption<SeverityFilter> value) => Refilter();
    partial void OnCategoryChanged(FilterOption<FindingCategory?> value) => Refilter();
    partial void OnScopeChanged(FilterOption<PathScope?> value) => Refilter();
    partial void OnPerspectiveChanged(FilterOption<Perspective?> value) => Refilter();

    partial void OnSelectedChanged(FindingRowViewModel? oldValue, FindingRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        Detail = newValue is null ? null : new FindingDetailViewModel(newValue.Group, Navigator);
    }

    /// <summary>Arrive from another page: reset the filters to show what was asked for, and select it.</summary>
    public void Apply(FindingsQuery query)
    {
        _applying = true;
        var group = query.RootCause is { } cause ? Result?.GroupOf(cause) : null;
        Severity = SeverityOptions.First(o => o.Value ==
            (query.NotesOnly || group?.Severity == Core.Detection.Severity.Info ? SeverityFilter.Notes : SeverityFilter.Problems));
        Category = CategoryOptions.First(o => o.Value == query.Category);
        Scope = ScopeOptions[0];
        Perspective = PerspectiveOptions[0];
        _applying = false;
        Refilter(select: group?.RootCause);
    }

    bool _applying;

    protected override void Rebuild(ScanResult? result) => Refilter(select: Selected?.Group.RootCause);

    void Refilter(string? select = null)
    {
        if (_applying) return;
        var groups = Result?.Diagnosis.Groups ?? [];
        var shown = groups.Where(Matches).ToList();
        Rows = shown.Select(g => new FindingRowViewModel(g, this)).ToList();

        var problems = groups.Count(g => g.Severity > Core.Detection.Severity.Info);
        var notes = groups.Count - problems;
        var all = $"{Words(problems, "problem")}, {Words(notes, "note")}";
        CountLine = Rows.Count == groups.Count ? all : $"Showing {Rows.Count} of {groups.Count} ({all})";

        Selected = (select is null ? null : Rows.FirstOrDefault(r => r.Group.RootCause == select)) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    bool Matches(FindingGroup group)
    {
        var s = group.Severity;
        var severityOk = Severity.Value switch
        {
            SeverityFilter.Problems => s > Core.Detection.Severity.Info,
            SeverityFilter.High => s == Core.Detection.Severity.High,
            SeverityFilter.Medium => s == Core.Detection.Severity.Medium,
            SeverityFilter.Low => s == Core.Detection.Severity.Low,
            SeverityFilter.Notes => s == Core.Detection.Severity.Info,
            _ => true,
        };
        return severityOk
               && (Category.Value is not { } c || group.Primary.Category == c)
               && (Scope.Value is not { } scope || group.Members.Any(f => f.Scope == scope || f.Entries.Any(e => e.Scope == scope)))
               && (Perspective.Value is not { } p || group.Members.Any(f => f.Perspectives.Contains(p)));
    }

    static string Words(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    internal void Select(FindingRowViewModel row) => Selected = row;
}

/// <summary>One root-cause group in the list.</summary>
public sealed partial class FindingRowViewModel(FindingGroup group, FindingsViewModel owner) : ViewModelBase
{
    public FindingGroup Group { get; } = group;

    [ObservableProperty] private bool _isSelected;

    public string Title => CharWrapTextBlock.BreakAfterSeparators(Group.Primary.Title);
    public IBrush SeverityBrush => Severities.BrushFor(Group.Severity);
    public string SeverityWord => Severities.Word(Group.Severity).ToUpperInvariant();

    /// <summary>"SEC-01 · Security · machine", plus how many other rules see the same cause.</summary>
    public string Meta
    {
        get
        {
            var others = Group.Related.Select(f => f.Rule).Distinct().Count(r => r != Group.Primary.Rule);
            return $"{Group.Primary.Rule} · {Group.Primary.Category} · {Severities.ScopeWord(Group.Primary.Scope)}" +
                   (others > 0 ? $" · +{others} related" : "");
        }
    }

    [RelayCommand]
    private void Open() => owner.Select(this);
}

/// <summary>The detail pane: one group, led by its worst finding, with the others under it.</summary>
public sealed partial class FindingDetailViewModel : ViewModelBase
{
    readonly INavigator _navigator;

    public FindingDetailViewModel(FindingGroup group, INavigator navigator)
    {
        Group = group;
        _navigator = navigator;
        Related = group.Related.Select(f => new RelatedFindingViewModel(f, this)).ToList();
        _finding = group.Primary;
    }

    public FindingGroup Group { get; }

    /// <summary>The finding the pane is showing: the group's lead, or a related one picked from the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(SeverityBrush), nameof(Badge), nameof(What), nameof(Why), nameof(Fix),
        nameof(Entries), nameof(HasEntries), nameof(Perspectives), nameof(HasPerspectives), nameof(Evidence), nameof(HasEvidence),
        nameof(LearnLabel), nameof(HasLearn), nameof(IsShowingLead), nameof(DetailsText))]
    private Finding _finding;

    public string Title => CharWrapTextBlock.BreakAfterSeparators(Finding.Title);
    public IBrush SeverityBrush => Severities.BrushFor(Finding.Severity);

    /// <summary>"HIGH · SEC-01 · Security · machine PATH".</summary>
    public string Badge => $"{Severities.Word(Finding.Severity).ToUpperInvariant()} · {Finding.Rule} · {Finding.Category} · " +
                           (Finding.Scope switch { PathScope.Machine => "machine PATH", PathScope.User => "user PATH", _ => "both PATHs" });

    public string What => Finding.What;
    public string Why => Finding.Why;
    public string Fix => Finding.Fix;

    public IReadOnlyList<EntryLinkViewModel> Entries => Finding.Entries.Select(e => new EntryLinkViewModel(e, _navigator)).ToList();
    public bool HasEntries => Finding.Entries.Count > 0;

    /// <summary>"A standard user, SYSTEM and you elevated".</summary>
    public string Perspectives
    {
        get
        {
            var names = Finding.Perspectives.Select(Severities.PerspectivePhrase).ToList();
            var text = names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
        }
    }
    public bool HasPerspectives => Finding.Perspectives.Count > 0;

    public IReadOnlyList<string> Evidence => Finding.Evidence;
    public bool HasEvidence => Finding.Evidence.Count > 0;

    public IReadOnlyList<RelatedFindingViewModel> Related { get; }
    public bool HasRelated => Related.Count > 0;
    public bool IsShowingLead => ReferenceEquals(Finding, Group.Primary);

    public string LearnLabel => Finding.Learn is { } topic && LearnLibrary.Find(topic) is { } article ? $"Learn: {article.Title}" : "";
    public bool HasLearn => Finding.Learn is { } topic && LearnLibrary.Find(topic) is not null;

    [RelayCommand]
    private void OpenLearn()
    {
        if (Finding.Learn is { } topic) _navigator.ToLearn(topic);
    }

    [RelayCommand]
    private void ShowLead() => Finding = Group.Primary;

    internal void Show(Finding finding) => Finding = finding;

    /// <summary>The finding as plain text, for a ticket or a message.</summary>
    public string DetailsText
    {
        get
        {
            var f = Finding;
            var sb = new StringBuilder();
            sb.AppendLine(f.Title);
            sb.AppendLine($"{f.Severity} · {f.Rule} · {f.Category}");
            sb.AppendLine();
            sb.AppendLine($"What: {f.What}");
            sb.AppendLine($"Why: {f.Why}");
            sb.AppendLine($"Fix: {f.Fix}");
            if (f.Entries.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Entries:");
                foreach (var e in f.Entries) sb.AppendLine($"  {e}");
            }
            if (f.Evidence.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Evidence:");
                foreach (var line in f.Evidence) sb.AppendLine($"  {line}");
            }
            if (Group.Related.Any())
            {
                sb.AppendLine();
                sb.AppendLine("Same cause:");
                foreach (var r in Group.Members.Where(m => !ReferenceEquals(m, f))) sb.AppendLine($"  {r.Rule}: {r.Title}");
            }
            sb.AppendLine();
            sb.AppendLine("— PATHology");
            return sb.ToString();
        }
    }
}

/// <summary>Another finding with the same root cause.</summary>
public sealed partial class RelatedFindingViewModel(Finding finding, FindingDetailViewModel owner) : ViewModelBase
{
    public Finding Finding { get; } = finding;
    public string Title => CharWrapTextBlock.BreakAfterSeparators(Finding.Title);
    public string Rule => Finding.Rule;
    public IBrush SeverityBrush => Severities.BrushFor(Finding.Severity);

    [RelayCommand]
    private void Open() => owner.Show(Finding);
}

/// <summary>An entry a finding names: "machine #3  C:\Tools", which opens it on the Entries page.</summary>
public sealed partial class EntryLinkViewModel(EntryRef entry, INavigator navigator) : ViewModelBase
{
    public string Position => $"{Severities.ScopeWord(entry.Scope)} #{entry.Index + 1}";
    public string Display => string.IsNullOrWhiteSpace(entry.Display) ? "(empty)" : entry.Display;

    [RelayCommand]
    private void Open() => navigator.ToEntry(entry.Scope, entry.Index);
}
