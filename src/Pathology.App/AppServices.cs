using Pathology.Core.Capture;
using Pathology.Core.Model;
using Pathology.Core.Settings;
using Pathology.Core.Store;
using Pathology.Windows;

namespace Pathology.App;

/// <summary>
/// Composition root: wires the real <c>Pathology.Core</c> graph the UI drives. The UI holds only these
/// services and never re-implements logic. Blocking calls (the machine scan) run off the UI thread via
/// <see cref="RunAsync{T}"/>.
/// </summary>
/// <remarks>
/// This is the one place real machine-facing implementations are chosen. Everything else takes them as
/// constructor arguments, so a test or the headless renderer can substitute fakes and a temp store.
/// </remarks>
public sealed class AppServices : IDisposable
{
    readonly Lazy<AccessEvaluator> _evaluator = new(() => new AccessEvaluator());
    readonly Lazy<SnapshotCapturer> _capturer;

    public IPathologyPaths Paths { get; }
    public ISettingsStore Settings { get; }

    /// <param name="root">Store root; null means this profile's real store.</param>
    public AppServices(string? root = null)
    {
        Paths = new PathologyPaths(root);
        Settings = new FileSettingsStore(Paths);
        _capturer = new(() => WindowsCapture.Create(_evaluator.Value));
    }

    /// <summary>
    /// Scan this machine's PATH into a snapshot. Read-only and side-effect free; network paths are probed only
    /// when the user has opted in (<see cref="PathologySettings.ProbeNetworkPaths"/>). Blocking: call it
    /// through <see cref="RunAsync{T}"/> from the UI.
    /// </summary>
    public PathSnapshot Capture(IProgress<CaptureProgress>? progress = null, CancellationToken cancel = default) =>
        Capture(new CaptureOptions { ProbeNetworkPaths = Settings.Get().ProbeNetworkPaths }, progress, cancel);

    /// <summary>As <see cref="Capture(IProgress{CaptureProgress}?, CancellationToken)"/>, with explicit options.</summary>
    public PathSnapshot Capture(CaptureOptions options, IProgress<CaptureProgress>? progress = null, CancellationToken cancel = default) =>
        _capturer.Value.Capture(options, progress, cancel);

    /// <summary>Run a blocking Core call on a background thread (keeps the UI responsive).</summary>
    public static Task<T> RunAsync<T>(Func<T> work) => Task.Run(work);

    public static Task RunAsync(Action work) => Task.Run(work);

    public void Dispose()
    {
        if (_evaluator.IsValueCreated) _evaluator.Value.Dispose();
    }
}
