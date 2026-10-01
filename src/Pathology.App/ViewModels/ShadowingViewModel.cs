using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Controls;
using Pathology.App.Scanning;
using Pathology.App.Theming;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Shadowing;
using static Pathology.App.Theme;

namespace Pathology.App.ViewModels;

/// <summary>
/// Which file actually runs when you type a command, and which copies it hides: a "which" search over the shadow
/// report, the commands more than one folder provides, and the Windows built-ins that are, or could be, shadowed.
/// </summary>
public sealed partial class ShadowingViewModel : ScanPageViewModel
{
    public ShadowingViewModel(ScanSession session, INavigator navigator) : base(session, navigator) => Rebuild(session.Current);

    public override string Title => "Shadowing";

    [ObservableProperty] private string _query = "";

    [ObservableProperty] private ResolutionViewModel? _resolution;
    [ObservableProperty] private string _queryNote = "";

    /// <summary>Riskiest first (a writable winner), or alphabetical.</summary>
    [ObservableProperty] private bool _sortByRisk = true;

    [ObservableProperty] private IReadOnlyList<CompetingRowViewModel> _competing = [];
    [ObservableProperty] private string _competingNote = "";

    [ObservableProperty] private IReadOnlyList<ShadowedBuiltinViewModel> _shadowedBuiltins = [];
    [ObservableProperty] private IReadOnlyList<ProblemRowViewModel> _atRisk = [];

    [ObservableProperty] private string _unlistedNote = "";

    public bool HasResolution => Resolution is not null;
    public bool HasQueryNote => QueryNote.Length > 0;
    public bool HasShadowedBuiltins => ShadowedBuiltins.Count > 0;
    public bool HasAtRisk => AtRisk.Count > 0;
    public bool BuiltinsSafe => HasResult && !HasShadowedBuiltins && !HasAtRisk;
    public bool HasUnlistedNote => UnlistedNote.Length > 0;

    partial void OnQueryChanged(string value) => Lookup();
    partial void OnSortByRiskChanged(bool value) => BuildCompeting();
    partial void OnResolutionChanged(ResolutionViewModel? value) => OnPropertyChanged(nameof(HasResolution));
    partial void OnQueryNoteChanged(string value) => OnPropertyChanged(nameof(HasQueryNote));

    protected override void Rebuild(ScanResult? result)
    {
        if (result is null)
        {
            Competing = [];
            ShadowedBuiltins = [];
            AtRisk = [];
            Resolution = null;
            Notify();
            return;
        }

        var report = result.Diagnosis.Shadows;
        BuildCompeting();

        // A Windows command that's beaten by something earlier on PATH: already shadowed, right now.
        ShadowedBuiltins = report.All
            .Where(r => !r.Winner.IsWindows && r.Hidden.Any(h => h.IsWindows))
            .Select(r => new ShadowedBuiltinViewModel(r, this))
            .ToList();
        // A writable folder ahead of System32 (SEC-04): every Windows command could be shadowed from it.
        AtRisk = result.Diagnosis.Groups
            .Where(g => g.Members.Any(f => f.Rule == "SEC-04"))
            .Select(g => new ProblemRowViewModel(g, Navigator))
            .ToList();

        UnlistedNote = report.UnlistedFolders.Count == 0 ? ""
            : $"{report.UnlistedFolders.Count} PATH folder{(report.UnlistedFolders.Count == 1 ? " wasn't" : "s weren't")} listed " +
              "(missing, on the network, or unreadable), so a command in them won't show here.";

        Lookup();
        Notify();
    }

    void Notify()
    {
        OnPropertyChanged(nameof(HasShadowedBuiltins));
        OnPropertyChanged(nameof(HasAtRisk));
        OnPropertyChanged(nameof(BuiltinsSafe));
        OnPropertyChanged(nameof(HasUnlistedNote));
    }

    void BuildCompeting()
    {
        if (Result is not { } result) return;
        var competing = result.Diagnosis.Shadows.Competing;
        if (!SortByRisk) competing = competing.OrderBy(r => r.Command, StringComparer.OrdinalIgnoreCase);
        Competing = competing.Select(r => new CompetingRowViewModel(r, this)).ToList();
        CompetingNote = Competing.Count == 0
            ? "Every command on PATH comes from one folder."
            : $"{Competing.Count} command{(Competing.Count == 1 ? " is" : "s are")} provided by more than one folder. The first folder wins.";
    }

    void Lookup()
    {
        var name = Query.Trim();
        if (name.Length == 0 || Result is not { } result)
        {
            Resolution = null;
            QueryNote = "";
            return;
        }
        var resolution = result.Diagnosis.Shadows.Resolve(name);
        Resolution = resolution is null ? null : new ResolutionViewModel(resolution);
        QueryNote = resolution is null ? $"Nothing on PATH provides \"{name}\"." : "";
    }

    /// <summary>Look a command up (from a list row, or another page).</summary>
    public void Show(string command) => Query = command;

    [RelayCommand]
    private void Clear() => Query = "";
}

/// <summary>How one name resolves: the winner, then everything it hides, in search order.</summary>
public sealed class ResolutionViewModel
{
    public ResolutionViewModel(CommandResolution resolution)
    {
        Command = resolution.Command;
        Chain = new[] { resolution.Winner }.Concat(resolution.Hidden)
            .Select((p, i) => new ProviderRowViewModel(p, i == 0))
            .ToList();
        Notes = resolution.Notes;
        Summary = resolution.Hidden.Count == 0
            ? $"Only one file answers to \"{Command}\"."
            : $"\"{Command}\" runs the first of these. The {(resolution.Hidden.Count == 1 ? "other is" : $"other {resolution.Hidden.Count} are")} hidden behind it.";
    }

    public string Command { get; }
    public string Summary { get; }
    public IReadOnlyList<ProviderRowViewModel> Chain { get; }
    public IReadOnlyList<string> Notes { get; }
    public bool HasNotes => Notes.Count > 0;
}

/// <summary>One file that could answer to a command.</summary>
public sealed class ProviderRowViewModel(CommandProvider provider, bool wins)
{
    public string FullPath => provider.FullPath;
    public string Label => wins ? "RUNS" : "hidden";
    public IBrush LabelBrush => wins ? Brush("AccentBrush") : Brush("MutedBrush");

    /// <summary>"machine #3 · .EXE · Windows".</summary>
    public string Meta
    {
        get
        {
            var parts = new List<string> { $"{Severities.ScopeWord(provider.Entry.Scope)} #{provider.Entry.Index + 1}", provider.Extension };
            if (provider.IsWindows) parts.Add("Windows");
            return string.Join(" · ", parts);
        }
    }

    public string Risk => provider.WritableByOthers ? "any user could replace it"
        : provider.WritableByYou ? "you could replace it without elevating" : "";

    public IBrush RiskBrush => provider.WritableByOthers ? Severities.BrushFor(Severity.High) : Severities.BrushFor(Severity.Medium);
    public bool HasRisk => Risk.Length > 0;
}

/// <summary>A command with more than one provider.</summary>
public sealed partial class CompetingRowViewModel(CommandResolution resolution, ShadowingViewModel owner) : ViewModelBase
{
    public string Command => resolution.Command;
    public string Winner => CharWrapTextBlock.BreakAfterSeparators(resolution.Winner.Folder);

    public string Hidden
    {
        get
        {
            var n = resolution.HiddenElsewhere.Count();
            return $"hides {n} other{(n == 1 ? "" : "s")}";
        }
    }

    public bool WinnerWritable => resolution.Winner.WritableByOthers || resolution.Winner.WritableByYou;

    public IBrush DotBrush => resolution.Winner.WritableByOthers ? Severities.BrushFor(Severity.High)
        : resolution.Winner.WritableByYou ? Severities.BrushFor(Severity.Medium) : Brushes.Transparent;

    [RelayCommand]
    private void Open() => owner.Show(Command);
}

/// <summary>A Windows command that something earlier on PATH already beats.</summary>
public sealed partial class ShadowedBuiltinViewModel(CommandResolution resolution, ShadowingViewModel owner) : ViewModelBase
{
    public string Command => resolution.Command;
    public string By => CharWrapTextBlock.BreakAfterSeparators(resolution.Winner.FullPath);
    public string Windows => CharWrapTextBlock.BreakAfterSeparators(resolution.Hidden.First(h => h.IsWindows).FullPath);

    [RelayCommand]
    private void Open() => owner.Show(Command);
}
