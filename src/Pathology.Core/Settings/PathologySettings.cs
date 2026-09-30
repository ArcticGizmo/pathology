namespace Pathology.Core.Settings;

/// <summary>
/// User-configurable, machine-local settings (persisted to <c>%LOCALAPPDATA%\PATHology Data\settings.json</c>).
/// </summary>
/// <remarks>
/// Only settings that do something today live here. Scan-on-launch arrives with the launch scan in M4 — see
/// docs/implementation-plan.md.
/// </remarks>
public sealed class PathologySettings
{
    /// <summary>Show the "what's new" changelog window on the first launch after an update.</summary>
    public bool ShowChangelogOnUpdate { get; set; } = true;

    /// <summary>
    /// Probe UNC paths and mapped network drives during a scan. <b>Off by default</b>: opening
    /// <c>\\host\share</c> authenticates to that host, handing whoever runs it an NTLM hash. When off, network
    /// entries are classified from their text alone.
    /// </summary>
    public bool ProbeNetworkPaths { get; set; }

    /// <summary>
    /// The app version that last ran here — used to pick the "what's new" entries. Null on a fresh
    /// install (nothing to diff against). Updated to the running version once the check has run.
    /// </summary>
    public string? LastSeenVersion { get; set; }

    public PathologySettings Clone() => new()
    {
        ShowChangelogOnUpdate = ShowChangelogOnUpdate,
        ProbeNetworkPaths = ProbeNetworkPaths,
        LastSeenVersion = LastSeenVersion,
    };
}
