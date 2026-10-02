using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Scanning;

namespace Pathology.App.ViewModels;

/// <summary>
/// A page drawn from the shared scan (Health, Findings, Entries, Shadowing). It rebuilds whenever the session
/// publishes a new result, and every one carries the same Re-scan button.
/// </summary>
public abstract partial class ScanPageViewModel : PageViewModel
{
    protected ScanPageViewModel(ScanSession session, INavigator navigator)
    {
        Session = session;
        Navigator = navigator;
        session.PropertyChanged += OnSessionChanged;
    }

    public ScanSession Session { get; }
    protected INavigator Navigator { get; }

    public ScanResult? Result => Session.Current;
    public bool HasResult => Session.Current is not null;
    public bool IsScanning => Session.IsScanning;

    /// <summary>No result yet and nothing running: the scan-on-launch setting is off, or the scan failed.</summary>
    public bool IsIdle => !HasResult && !IsScanning;

    public string? ScanError => Session.Error;
    public bool HasScanError => Session.Error is not null;

    public string RescanLabel => HasResult ? "Re-scan" : "Scan";

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task Rescan() => Session.ScanAsync();

    bool CanRescan() => !Session.IsScanning;

    void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScanSession.Current):
                Rebuild(Session.Current);
                OnPropertyChanged(nameof(Result));
                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(RescanLabel));
                break;
            case nameof(ScanSession.IsScanning):
                OnPropertyChanged(nameof(IsScanning));
                OnPropertyChanged(nameof(IsIdle));
                RescanCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ScanSession.Error):
                OnPropertyChanged(nameof(ScanError));
                OnPropertyChanged(nameof(HasScanError));
                break;
        }
    }

    /// <summary>Rebuild the page from a new result (null: nothing scanned yet).</summary>
    protected abstract void Rebuild(ScanResult? result);
}
