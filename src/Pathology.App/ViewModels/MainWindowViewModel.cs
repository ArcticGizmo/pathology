using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Updates;
using Pathology.Core.Store;

namespace Pathology.App.ViewModels;

/// <summary>
/// The app shell: owns the pages, the left-nav rows, the update button, and which page is showing.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    readonly PageViewModel _health;
    readonly PageViewModel _findings;
    readonly PageViewModel _entries;
    readonly PageViewModel _shadowing;
    readonly PageViewModel _learn;

    /// <summary>Every navigable page — the nav list plus the bottom-pinned Settings and About.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Left-nav rows: page entries interleaved with section headers ("Diagnose" / "Understand").</summary>
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

    public MainWindowViewModel(AppServices services)
    {
        _health = new PlaceholderPageViewModel("Health", "PATH health",
            "One number for how safe and tidy this machine's PATH is — and the one fix that moves it most.",
            "Arrives in M4, once the scanner (M1), detectors (M2) and scoring (M3) exist.",
            [
                "A health ring: 0–100%, capped below 50% while any critical finding stands",
                "Security, Correctness and Hygiene sub-scores",
                "The top findings, and how far fixing the worst would lift the score",
                "UAC exposure, length headroom, and what the scan looked at",
            ]);
        _findings = new PlaceholderPageViewModel("Findings", "Findings",
            "Every problem found, worst first, each with what it is, why it matters and how to fix it.",
            "Arrives in M4.",
            [
                "Filters by severity, category, scope and perspective",
                "Root-cause groups — one drive-root ACL reported once, not per folder",
                "The ACE or owner that makes a folder writable",
            ]);
        _entries = new PlaceholderPageViewModel("Entries", "PATH entries",
            "The machine and user PATH, in the order Windows searches them.",
            "Arrives in M4.",
            [
                "Raw and expanded values, and the registry value kind",
                "Who can write each folder: you, you elevated, SYSTEM, a standard user",
                "Owners, junction targets, drive types and hygiene defects",
            ]);
        _shadowing = new PlaceholderPageViewModel("Shadowing", "Shadowing",
            "Which executable actually runs when you type a command — and which copies it hides.",
            "Arrives in M4.",
            [
                "A \"which\" search that walks PATH and PATHEXT the way cmd does",
                "Commands provided by more than one folder",
                "Built-ins that a writable, earlier folder could shadow",
            ]);
        _learn = new PlaceholderPageViewModel("Learn", "Learn",
            "Short notes on how Windows really resolves commands and DLLs.",
            "Arrives in M4.",
            [
                "DLL search order and phantom directories",
                "PATHEXT precedence",
                "How UAC and elevated sessions inherit PATH",
                "REG_SZ vs REG_EXPAND_SZ, and why not setx",
            ]);
        Settings = new SettingsViewModel(services);
        About = new AboutViewModel();

        // Settings + About are navigable (so they get active-state highlighting) but live in the bottom nav
        // slot rather than the diagnostic list, so they're not in NavItems.
        Pages = [_health, _findings, _entries, _shadowing, _learn, Settings, About];

        NavItems.Add(new NavHeaderViewModel("Diagnose"));
        NavItems.Add(_health);
        NavItems.Add(_findings);
        NavItems.Add(_entries);
        NavItems.Add(_shadowing);
        NavItems.Add(new NavHeaderViewModel("Understand"));
        NavItems.Add(_learn);

        // Land on Health — the score is the front door.
        _currentPage = _health;
        _health.IsActive = true;

        _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => CurrentPage = page;

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
