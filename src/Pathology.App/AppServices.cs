using Pathology.Core.Settings;
using Pathology.Core.Store;

namespace Pathology.App;

/// <summary>
/// Composition root: wires the real <c>Pathology.Core</c> graph the UI drives. The UI holds only these
/// services and never re-implements logic. Blocking calls (from M1, the machine scan) run off the UI thread
/// via <see cref="RunAsync{T}"/>.
/// </summary>
/// <remarks>
/// This is the one place real machine-facing implementations are chosen. Everything else takes them as
/// constructor arguments, so a test or the headless renderer can substitute fakes and a temp store.
/// </remarks>
public sealed class AppServices
{
    public IPathologyPaths Paths { get; }
    public ISettingsStore Settings { get; }

    /// <param name="root">Store root; null means this profile's real store.</param>
    public AppServices(string? root = null)
    {
        Paths = new PathologyPaths(root);
        Settings = new FileSettingsStore(Paths);
    }

    /// <summary>Run a blocking Core call on a background thread (keeps the UI responsive).</summary>
    public static Task<T> RunAsync<T>(Func<T> work) => Task.Run(work);

    public static Task RunAsync(Action work) => Task.Run(work);
}
