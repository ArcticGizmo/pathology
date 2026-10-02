using CommunityToolkit.Mvvm.ComponentModel;
using Pathology.App.Scanning;
using Pathology.Core.Settings;

namespace Pathology.App.ViewModels;

/// <summary>
/// The Settings page (pinned to the bottom nav): scan on launch, the network-probing opt-in, the changelog
/// toggle, and exporting a redacted snapshot for a bug report.
/// </summary>
public partial class SettingsViewModel : PageViewModel
{
    readonly AppServices _services;
    readonly ScanSession _session;

    public override string Title => "Settings";

    /// <summary>Suppresses the persist-on-change handlers while fields are being loaded from the store.</summary>
    bool _loading;

    [ObservableProperty] private bool _scanOnLaunch;

    [ObservableProperty] private bool _showChangelogOnUpdate;

    /// <summary>Probe UNC paths and mapped network drives (off by default: it leaks an NTLM hash to the host).</summary>
    [ObservableProperty] private bool _probeNetworkPaths;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportStatus))]
    private string _exportStatus = "";

    [ObservableProperty] private bool _exportFailed;

    public bool HasExportStatus => ExportStatus.Length > 0;

    /// <summary>Export needs something to export.</summary>
    public bool CanExport => _session.Current is not null;

    public SettingsViewModel(AppServices services, ScanSession session)
    {
        _services = services;
        _session = session;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanSession.Current)) OnPropertyChanged(nameof(CanExport));
        };
        LoadFromStore();
    }

    protected override void OnActivated() => LoadFromStore();

    void LoadFromStore()
    {
        var s = _services.Settings.Get();
        _loading = true;
        ScanOnLaunch = s.ScanOnLaunch;
        ShowChangelogOnUpdate = s.ShowChangelogOnUpdate;
        ProbeNetworkPaths = s.ProbeNetworkPaths;
        _loading = false;
    }

    // Toggles persist immediately — there's nothing to validate about them.
    partial void OnScanOnLaunchChanged(bool value) => Persist(s => s.ScanOnLaunch = value);

    partial void OnShowChangelogOnUpdateChanged(bool value) => Persist(s => s.ShowChangelogOnUpdate = value);

    partial void OnProbeNetworkPathsChanged(bool value) => Persist(s => s.ProbeNetworkPaths = value);

    void Persist(Action<PathologySettings> change)
    {
        if (_loading) return;
        var s = _services.Settings.Get();
        change(s);
        _services.Settings.Save(s);
    }

    /// <summary>Write the latest scan, redacted, to a file the user picked.</summary>
    public void Export(string path)
    {
        if (_session.Current is not { } result) return;
        try
        {
            var refused = SnapshotExport.Write(result.Snapshot, path);
            ExportFailed = refused is not null;
            ExportStatus = refused ?? $"Saved to {path}. Usernames, SIDs, the PC's name and profile folders are replaced with placeholders.";
        }
        catch (Exception ex)
        {
            ExportFailed = true;
            ExportStatus = $"Couldn't save it: {ex.Message}";
        }
    }
}
