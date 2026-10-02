using CommunityToolkit.Mvvm.ComponentModel;
using Pathology.Core.Capture;
using Pathology.Core.Model;

namespace Pathology.App.Scanning;

/// <summary>
/// The scan every page shares: runs one on launch and on Re-scan, reports its progress as a checklist, and
/// holds the latest <see cref="ScanResult"/>. Pages watch <see cref="Current"/> and rebuild from it.
/// </summary>
/// <remarks>
/// The capture itself is injected, so a test or the headless renderer never scans the real machine: they
/// hand the session a result built from a made-up snapshot with <see cref="Show"/>.
/// </remarks>
public sealed partial class ScanSession : ObservableObject
{
    readonly Func<IProgress<CaptureProgress>, CancellationToken, PathSnapshot> _capture;

    /// <param name="capture">Takes a snapshot. Blocking; the session runs it off the UI thread.</param>
    public ScanSession(Func<IProgress<CaptureProgress>, CancellationToken, PathSnapshot> capture) => _capture = capture;

    /// <summary>The latest scan, or null before the first one finishes.</summary>
    [ObservableProperty] private ScanResult? _current;

    [ObservableProperty] private bool _isScanning;

    /// <summary>Why the last scan failed, or null.</summary>
    [ObservableProperty] private string? _error;

    public ScanProgressViewModel Progress { get; } = new();

    /// <summary>Scan the machine (read-only) and publish the result. A second call while one runs is ignored.</summary>
    public async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        Error = null;
        Progress.Reset();

        // Created here, on the UI thread, so its reports are marshalled back to it.
        var progress = new Progress<CaptureProgress>(Progress.Apply);
        try
        {
            var result = await AppServices.RunAsync(() => ScanResult.Of(_capture(progress, CancellationToken.None)));
            Progress.Complete();
            Current = result;
        }
        catch (Exception ex)
        {
            Progress.Fail();
            Error = ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Publish a result without scanning: a posed snapshot (the renderer, tests).</summary>
    public void Show(ScanResult result)
    {
        Progress.Complete();
        Current = result;
    }
}
