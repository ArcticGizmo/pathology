using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Changelog;
using Pathology.App.Updates;
using Pathology.App.Views;
using Pathology.Core.Changelog;

namespace Pathology.App.ViewModels;

/// <summary>
/// The "About" page (pinned to the bottom of the nav): app version, links to the repo and its issue
/// tracker, a "what's new" changelog viewer, and a manual check-for-updates / install flow over the
/// same feed the launch check uses.
/// </summary>
public partial class AboutViewModel : PageViewModel
{
    public const string RepoUrl = "https://github.com/ArcticGizmo/pathology";
    public const string IssuesUrl = RepoUrl + "/issues";

    public override string Title => "About";

    /// <summary>The running version, e.g. "0.1.0" (from the assembly's informational version).</summary>
    public string Version => Current;

    /// <summary>The running version — shared so the launch-time changelog check reads the same value.</summary>
    public static string Current { get; } = ResolveVersion();

    /// <summary>Result of the last check — held so <see cref="ApplyUpdateCommand"/> can install it.</summary>
    UpdateCheckResult? _lastCheck;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _updateStatus = "";

    /// <summary>True once a check found an installable update — reveals the install button.</summary>
    [ObservableProperty] private bool _updateAvailable;

    [RelayCommand] private void OpenRepo() => OpenUrl(RepoUrl);
    [RelayCommand] private void OpenIssues() => OpenUrl(IssuesUrl);

    /// <summary>Open the changelog in the same window the post-update popup uses (no suppress action here).</summary>
    [RelayCommand]
    private void ViewChangelog()
    {
        if (ChangelogMarkdown.LoadEmbedded() is not { } markdown) return;
        var sections = ChangelogParser.Parse(markdown);
        new ChangelogWindow("What's new in PATHology", "Recent releases", sections).Show();
    }

    bool NotBusy => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CheckForUpdates()
    {
        IsBusy = true;
        UpdateAvailable = false;
        UpdateStatus = "Checking for updates…";
        try
        {
            _lastCheck = await UpdateChecker.CheckDetailedAsync();
            UpdateStatus = _lastCheck.Availability switch
            {
                UpdateAvailability.Available =>
                    $"v{_lastCheck.AvailableVersion} is available — you have v{_lastCheck.CurrentVersion}.",
                UpdateAvailability.UpToDate => "You're on the latest version.",
                UpdateAvailability.NotApplicable =>
                    "This build wasn't installed by the installer, so it can't update itself.",
                _ => "Couldn't reach the update feed.",
            };
            UpdateAvailable = _lastCheck.Availability == UpdateAvailability.Available;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ApplyUpdate()
    {
        if (_lastCheck is null) return;
        IsBusy = true;
        UpdateStatus = "Downloading and installing — PATHology will restart…";
        try
        {
            await UpdateChecker.ApplyAsync(_lastCheck);
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Couldn't install the update: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser, or the user's shell said no — not worth a dialog */ }
    }

    /// <summary>
    /// The informational version with any build metadata (a "+sha" suffix) trimmed, so it reads as a
    /// plain version number rather than a build stamp.
    /// </summary>
    static string ResolveVersion()
    {
        var informational = typeof(AboutViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is { Length: > 0 })
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
        return typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
