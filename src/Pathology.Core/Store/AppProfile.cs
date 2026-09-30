namespace Pathology.Core.Store;

/// <summary>
/// Tells a development instance apart from an installed release, so the two never share a store
/// (a dev run must not clobber the settings — and, from M6, the PATH backups — your installed copy keeps).
///
/// The decision (mirrors emuwren's AppProfile): the <c>PATHOLOGY_DEV</c> environment variable wins if set,
/// otherwise the build configuration decides — a Debug build (F5 / <c>dotnet run</c> / <c>dotnet test</c>)
/// is a dev instance, a Release build (what the installer ships) is not. Computed once at startup.
/// </summary>
public static class AppProfile
{
    /// <summary>True when this process is an isolated development instance.</summary>
    public static bool IsDev { get; } = ComputeIsDev();

    /// <summary>
    /// The store folder under %LOCALAPPDATA% for this profile — <c>PATHology Data</c> or
    /// <c>PATHology Data (Dev)</c>. Not plain <c>PATHology</c>: paths are case-insensitive, so that would be
    /// Velopack's install folder (<c>%LOCALAPPDATA%\Pathology</c>), which an uninstall removes wholesale —
    /// taking settings (and, from M6, the PATH backups a rollback needs) with it.
    /// </summary>
    public static string DataFolderName => IsDev ? "PATHology Data (Dev)" : "PATHology Data";

    /// <summary>Suffix for user-facing labels (e.g. the window title) — <c>""</c> or <c>" (Dev)"</c>.</summary>
    public static string DisplaySuffix => IsDev ? " (Dev)" : "";

    static bool ComputeIsDev()
    {
        // PATHOLOGY_DEV overrides the build default: "0"/"false" forces release, any other value forces dev.
        var env = Environment.GetEnvironmentVariable("PATHOLOGY_DEV");
        if (!string.IsNullOrEmpty(env))
            return !(env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase));
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
