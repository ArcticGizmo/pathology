namespace Pathology.Core.Settings;

/// <summary>
/// User-configurable, machine-local settings (persisted to <c>%LOCALAPPDATA%\PATHology Data\settings.json</c>).
/// </summary>
/// <remarks>
/// Only settings that do something today live here. Scan-on-launch and the (off by default) network-path
/// probing arrive with the scanner in M1 — see docs/implementation-plan.md.
/// </remarks>
public sealed class PathologySettings
{
    /// <summary>Show the "what's new" changelog window on the first launch after an update.</summary>
    public bool ShowChangelogOnUpdate { get; set; } = true;

    /// <summary>
    /// The app version that last ran here — used to pick the "what's new" entries. Null on a fresh
    /// install (nothing to diff against). Updated to the running version once the check has run.
    /// </summary>
    public string? LastSeenVersion { get; set; }

    public PathologySettings Clone() => new()
    {
        ShowChangelogOnUpdate = ShowChangelogOnUpdate,
        LastSeenVersion = LastSeenVersion,
    };
}
