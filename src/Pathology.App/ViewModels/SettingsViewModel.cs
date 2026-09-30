using CommunityToolkit.Mvvm.ComponentModel;
using Pathology.Core.Settings;

namespace Pathology.App.ViewModels;

/// <summary>
/// The Settings page (pinned to the bottom nav). In M0 it holds just the changelog toggle; the scan
/// settings (scan on launch, network-path probing) join it with the scanner in M1.
/// </summary>
public partial class SettingsViewModel : PageViewModel
{
    readonly AppServices _services;

    public override string Title => "Settings";

    /// <summary>Suppresses the persist-on-change handlers while fields are being loaded from the store.</summary>
    bool _loading;

    [ObservableProperty] private bool _showChangelogOnUpdate;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        LoadFromStore();
    }

    protected override void OnActivated() => LoadFromStore();

    void LoadFromStore()
    {
        var s = _services.Settings.Get();
        _loading = true;
        ShowChangelogOnUpdate = s.ShowChangelogOnUpdate;
        _loading = false;
    }

    // Toggles persist immediately — there's nothing to validate about them.
    partial void OnShowChangelogOnUpdateChanged(bool value) => Persist(s => s.ShowChangelogOnUpdate = value);

    void Persist(Action<PathologySettings> change)
    {
        if (_loading) return;
        var s = _services.Settings.Get();
        change(s);
        _services.Settings.Save(s);
    }
}
