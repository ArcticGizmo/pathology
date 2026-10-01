using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Repair;
using Pathology.App.Scanning;
using Pathology.App.Updates;
using Pathology.Core.Model;
using Pathology.Core.Store;

namespace Pathology.App.ViewModels;

/// <summary>
/// The app shell: owns the pages, the left-nav rows, the update button, and which page is showing. It's also
/// the <see cref="INavigator"/> pages use to send you to one another.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase, INavigator
{
    readonly DashboardViewModel _dashboard;
    readonly EntriesViewModel _system;
    readonly EntriesViewModel _user;
    readonly ShadowingViewModel _shadowing;
    readonly HistoryViewModel _history;
    readonly ReviewViewModel _review;
    readonly LearnViewModel _learn;

    /// <summary>The scan every page reads.</summary>
    public ScanSession Session { get; }

    /// <summary>What's staged on the System and User pages, which Review applies.</summary>
    public PendingChanges Pending { get; }

    /// <summary>Every navigable page: the nav list, Review (reached from the pending bar), and the bottom-pinned pages.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Left-nav rows: the pages, with System and User nested under an "Entries" group.</summary>
    public ObservableCollection<object> NavItems { get; } = [];

    /// <summary>Learn: a bonus, so it sits with Settings and About at the bottom of the nav.</summary>
    public LearnViewModel Learn => _learn;

    /// <summary>The Settings page — pinned to the bottom of the nav.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The About page — pinned to the bottom of the nav.</summary>
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
    /// What Review and History apply through. The app passes the real one (<see cref="AppServices.Repair"/>); the
    /// renderer and tests pass a stand-in that refuses to write.
    /// </param>
    /// <param name="checkForUpdates">Run the launch update check (off for the renderer, which poses the button).</param>
    public MainWindowViewModel(AppServices services, ScanSession session, IRepairService repair, bool checkForUpdates = true)
    {
        Session = session;
        // First, so it has re-planned against a new scan before any page redraws from it.
        Pending = new PendingChanges(session, repair);
        _dashboard = new DashboardViewModel(session, this, Pending);
        _system = new EntriesViewModel(PathScope.Machine, session, this, Pending);
        _user = new EntriesViewModel(PathScope.User, session, this, Pending);
        _shadowing = new ShadowingViewModel(session, this);
        _history = new HistoryViewModel(repair, session);
        _review = new ReviewViewModel(Pending, this, applied: _history.Reload);
        _learn = new LearnViewModel();
        Settings = new SettingsViewModel(services, session);
        About = new AboutViewModel();

        Pages = [_dashboard, _system, _user, _shadowing, _history, _review, _learn, Settings, About];

        NavItems.Add(_dashboard);
        NavItems.Add(new NavGroupViewModel("Entries", [_system, _user]));
        NavItems.Add(_system);
        NavItems.Add(_user);
        NavItems.Add(_shadowing);
        NavItems.Add(_history);

        // Land on the Dashboard: the ratings and what can be fixed are the front door.
        _currentPage = _dashboard;
        _dashboard.IsActive = true;

        if (checkForUpdates) _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => CurrentPage = page;

    /// <summary>The "Entries" group row: open its first page.</summary>
    [RelayCommand]
    private void OpenGroup(NavGroupViewModel group) => CurrentPage = group.Pages[0];

    EntriesViewModel EntriesFor(PathScope scope) => scope == PathScope.Machine ? _system : _user;

    public void ToEntry(PathScope scope, int index)
    {
        // Its own PATH lists it even once it's staged to move away (struck through); the other is a fallback.
        var page = EntriesFor(scope);
        if (!page.Select(scope, index) && EntriesFor(Other(scope)).Select(scope, index)) page = EntriesFor(Other(scope));
        CurrentPage = page;
    }

    static PathScope Other(PathScope scope) => scope == PathScope.Machine ? PathScope.User : PathScope.Machine;

    public void ToEntries(PathScope scope) => CurrentPage = EntriesFor(scope);

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

    public void ToReview() => CurrentPage = _review;

    // The pages, for the renderer to pose.
    public DashboardViewModel Dashboard => _dashboard;
    public EntriesViewModel SystemEntries => _system;
    public EntriesViewModel UserEntries => _user;
    public ShadowingViewModel Shadowing => _shadowing;
    public HistoryViewModel History => _history;
    public ReviewViewModel Review => _review;

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
