using Pathology.Core.Store;

namespace Pathology.Tests;

/// <summary>
/// A throwaway store root under the temp directory, deleted on dispose — so a test never reads or
/// writes the real <c>%LOCALAPPDATA%\PATHology Data</c>.
/// </summary>
public sealed class TempStore : IDisposable
{
    public TempStore()
    {
        Root = Path.Combine(Path.GetTempPath(), "pathology-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new PathologyPaths(Root);
    }

    public string Root { get; }
    public IPathologyPaths Paths { get; }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* a locked temp dir isn't a test failure */ }
    }
}
