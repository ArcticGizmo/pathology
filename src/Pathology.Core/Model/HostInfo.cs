namespace Pathology.Core.Model;

/// <summary>How this process's token relates to UAC (<c>TOKEN_ELEVATION_TYPE</c>).</summary>
public enum ElevationType
{
    Unknown,

    /// <summary>No split token: UAC is off, or the account isn't an administrator.</summary>
    Default,

    /// <summary>Running elevated; the linked token is the filtered one.</summary>
    Full,

    /// <summary>Running filtered; the linked token is the elevated one.</summary>
    Limited,
}

/// <summary>
/// Who and where the snapshot was taken. Every identifying field here is replaced by
/// <see cref="Redaction.SnapshotRedactor"/> before a snapshot leaves the machine.
/// </summary>
public sealed record HostInfo
{
    public string MachineName { get; init; } = "";
    public string UserName { get; init; } = "";
    public string UserDomain { get; init; } = "";
    public string UserSid { get; init; } = "";
    public string UserProfile { get; init; } = "";

    /// <summary>e.g. <c>10.0.26200.6584</c>.</summary>
    public string OsVersion { get; init; } = "";

    /// <summary>e.g. <c>25H2</c>, or null.</summary>
    public string? OsDisplayVersion { get; init; }

    public ElevationType Elevation { get; init; }

    /// <summary>UAC's <c>EnableLUA</c> policy, or null when unset.</summary>
    public bool? EnableLua { get; init; }

    /// <summary>
    /// <c>EnableLinkedConnections</c>: when set, elevated sessions see the user's mapped drives. Null when unset.
    /// </summary>
    public bool? EnableLinkedConnections { get; init; }
}

/// <summary>Knobs for one capture.</summary>
public sealed record CaptureOptions
{
    /// <summary>
    /// Probe UNC paths and mapped network drives. <b>Off by default</b>: opening <c>\\host\share</c>
    /// authenticates to that host, which leaks an NTLM hash to whoever controls it.
    /// </summary>
    public bool ProbeNetworkPaths { get; init; }
}
