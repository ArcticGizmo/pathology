using Pathology.Core.Store;

namespace Pathology.Core.Settings;

/// <summary>Reads and writes <see cref="PathologySettings"/> from the store.</summary>
public interface ISettingsStore
{
    /// <summary>Current settings, or defaults if none have been saved.</summary>
    PathologySettings Get();

    /// <summary>Persist the settings.</summary>
    void Save(PathologySettings settings);
}

/// <summary>File-backed <see cref="ISettingsStore"/> (one JSON file at <see cref="IPathologyPaths.SettingsFile"/>).</summary>
public sealed class FileSettingsStore(IPathologyPaths paths) : ISettingsStore
{
    public PathologySettings Get() => JsonFile.Read<PathologySettings>(paths.SettingsFile) ?? new PathologySettings();

    public void Save(PathologySettings settings) => JsonFile.Write(paths.SettingsFile, settings.Clone());
}
