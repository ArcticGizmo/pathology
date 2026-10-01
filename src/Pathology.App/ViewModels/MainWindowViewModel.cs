using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Repair;
using Pathology.App.Scanning;
using Pathology.App.Updates;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Store;

namespace Pathology.App.ViewModels;

/// <summary>
/// The app shell: owns the pages, the left-nav rows, the update button, and which page is showing. It's also
/// the <see cref="INavigator"/> pages use to send you to one another.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase, INavigator
{
    readonly HealthViewModel _health;
    readonly FindingsViewModel _findings;
    readonly EntriesViewModel _entries;
    readonly ShadowingViewModel _shadowing;
    readonly FixViewModel _fix;
    readonly HistoryViewModel _history;
    readonly LearnViewModel _learn;

    /// <summary>The scan every page reads.</summary>
    public ScanSession Session { get; }

    /// <summary>Every navigable page — the nav list plus the bottom-pinned Settings and About.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Left-nav rows: page entries interleaved with section headers ("Diagnose" / "Repair" / "Understand").</summary>
    public ObservableCollection<object> NavItems { get; } = [];

    /// <summary>The Settings page — pinned to the bottom of the nav, outside the diagnostic sequence.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The About page — pinned to the bottom of the nav, outside the diagnostic sequence.</summary>
    public AboutViewModel About { get; }

    [ObservableProperty] private PageViewModel _currentPage;

    /// <summary>
    /// The version a newer release offers, or null. Drives the blue "Update to v…" button above Settings —
    /// a notification affordance that routes to About, where the download / install / restart actually
    /// happens (applying stays explicit and user-driven).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateButtonLabel))]
    private string? _availableVersion;

    public bool HasUpdate => AvailableVersion is { Length: > 0 };

    public string UpdateButtonLabel => $"Update to v{AvailableVersion}";

    /// <summary>True when this is an isolated dev instance — drives the pink "- DEV" nav badge.</summary>
    public bool IsDevInstance => AppProfile.IsDev;

    /// <summary>Window title for a session: the product name plus the dev badge.</summary>
    public static string TitleFor() => "PATHology" + AppProfile.DisplaySuffix;

    /// <param name="services">The composition root.</param>
    /// <param name="session">The shared scan. The app starts it; the renderer poses it.</param>
    /// <param name="repair">
    /// What Fix and History apply through. The app passes the real one (<see cref="AppServices.Repair"/>); the
    /// renderer and tests pass a stand-in that refuses to write.
    /// </param>
    /// <param name="checkForUpdates">Run the launch update check (off for the renderer, which poses the button).</param>
    public MainWindowViewModel(AppServices services, ScanSession session, IRepairService repair, bool checkForUpdates = true)
    {
        Session = session;
        _health = new HealthViewModel(session, this);
        _findings = new FindingsViewModel(session, this);
        _entries = new EntriesViewModel(session, this);
        _shadowing = new ShadowingViewModel(session, this);
        _history = new HistoryViewModel(repair, session);
        _fix = new FixViewModel(session, this, repair, applied: _history.Reload);
        _learn = new LearnViewModel();
        Settings = new SettingsViewModel(services, session);
        About = new AboutViewModel();

        // Settings + About are navigable (so they get active-state highlighting) but live in the bottom nav
        // slot rather than the diagnostic list, so they're not in NavItems.
        Pages = [_health, _findings, _entries, _shadowing, _fix, _history, _learn, Settings, About];

        NavItems.Add(new NavHeaderViewModel("Diagnose"));
        NavItems.Add(_health);
        NavItems.Add(_findings);
        NavItems.Add(_entries);
        NavItems.Add(_shadowing);
        NavItems.Add(new NavHeaderViewModel("Repair"));
        NavItems.Add(_fix);
        NavItems.Add(_history);
        NavItems.Add(new NavHeaderViewModel("Understand"));
        NavItems.Add(_learn);

        // Land on Health — the ratings are the front door.
        _currentPage = _health;
        _health.IsActive = true;

        session.PropertyChanged += OnSessionChanged;
        UpdateNavCounts(session.Current);

        if (checkForUpdates) _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => CurrentPage = page;

    public void ToFindings(FindingsQuery query)
    {
        _findings.Apply(query);
        CurrentPage = _findings;
    }

    public void ToEntry(PathScope scope, int index)
    {
        _entries.Select(scope, index);
        CurrentPage = _entries;
    }

    public void ToLearn(string topic)
    {
        _learn.Open(topic);
        CurrentPage = _learn;
    }

    public void ToCommand(string command)
    {
        _shadowing.Show(command);
        CurrentPage = _shadowing;
    }

    public void ToFix(PathScope scope, int index)
    {
        _fix.Show(scope, index);
        CurrentPage = _fix;
    }

    // The pages, for the renderer to pose.
    public HealthViewModel Health => _health;
    public FindingsViewModel Findings => _findings;
    public EntriesViewModel Entries => _entries;
    public ShadowingViewModel Shadowing => _shadowing;
    public FixViewModel Fix => _fix;
    public HistoryViewModel History => _history;
    public LearnViewModel Learn => _learn;

    void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanSession.Current)) UpdateNavCounts(Session.Current);
    }

    /// <summary>Findings' badge counts the High problems; Entries' counts the entries.</summary>
    void UpdateNavCounts(ScanResult? result)
    {
        _findings.NavCount = result?.Health.Count(Severity.High) ?? 0;
        _entries.NavCount = result?.Snapshot.Entries.Count ?? 0;
    }

    /// <summary>Take the user to About, where the update is downloaded, installed, and the app restarts.
    /// Kicks its check on arrival so the Install button is one click away.</summary>
    [RelayCommand]
    private async Task ShowUpdateAsync()
    {
        CurrentPage = About;
        if (About.CheckForUpdatesCommand.CanExecute(null))
            await About.CheckForUpdatesCommand.ExecuteAsync(null);
    }

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        foreach (var page in Pages)
            page.IsActive = ReferenceEquals(page, value);
    }

    async Task CheckForUpdatesAsync()
    {
        var result = await UpdateChecker.CheckDetailedAsync();
        if (result.Availability == UpdateAvailability.Available)
            AvailableVersion = result.AvailableVersion;
    }
}
