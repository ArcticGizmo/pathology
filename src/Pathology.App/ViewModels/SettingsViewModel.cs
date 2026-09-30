using CommunityToolkit.Mvvm.ComponentModel;
using Pathology.Core.Settings;

namespace Pathology.App.ViewModels;

/// <summary>
/// The Settings page (pinned to the bottom nav): the changelog toggle and the network-probing opt-in.
/// Scan-on-launch joins them with the launch scan in M4.
/// </summary>
public partial class SettingsViewModel : PageViewModel
{
    readonly AppServices _services;

    public override string Title => "Settings";

    /// <summary>Suppresses the persist-on-change handlers while fields are being loaded from the store.</summary>
    bool _loading;

    [ObservableProperty] private bool _showChangelogOnUpdate;

    /// <summary>Probe UNC paths and mapped network drives (off by default: it leaks an NTLM hash to the host).</summary>
    [ObservableProperty] private bool _probeNetworkPaths;

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
        ProbeNetworkPaths = s.ProbeNetworkPaths;
        _loading = false;
    }

    // Toggles persist immediately — there's nothing to validate about them.
    partial void OnShowChangelogOnUpdateChanged(bool value) => Persist(s => s.ShowChangelogOnUpdate = value);

    partial void OnProbeNetworkPathsChanged(bool value) => Persist(s => s.ProbeNetworkPaths = value);

    void Persist(Action<PathologySettings> change)
    {
        if (_loading) return;
        var s = _services.Settings.Get();
        change(s);
        _services.Settings.Save(s);
    }
}
