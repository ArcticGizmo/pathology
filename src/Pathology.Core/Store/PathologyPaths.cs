namespace Pathology.Core.Store;

/// <summary>
/// The machine-local store layout (default root <c>%LOCALAPPDATA%\PATHology Data</c>, or
/// <c>PATHology Data (Dev)</c> for a dev instance — see <see cref="AppProfile.DataFolderName"/> for why it
/// isn't Velopack's install folder). An interface so tests can point it at a temp dir.
/// </summary>
public interface IPathologyPaths
{
    /// <summary>Root of the store.</summary>
    string Root { get; }

    /// <summary>The user-settings file.</summary>
    string SettingsFile { get; }
}

/// <summary>Filesystem implementation of <see cref="IPathologyPaths"/>.</summary>
public sealed class PathologyPaths : IPathologyPaths
{
    public PathologyPaths(string? root = null)
        => Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.DataFolderName);

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
}
