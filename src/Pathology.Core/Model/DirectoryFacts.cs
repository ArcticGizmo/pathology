using System.Text.Json.Serialization;

namespace Pathology.Core.Model;

/// <summary>Where a directory lives, from <c>GetDriveType</c> (or the string alone, for UNC paths).</summary>
public enum DriveKind
{
    Unknown,
    Fixed,
    Removable,
    CdRom,
    RamDisk,

    /// <summary>A drive letter mapped to a network share. SYSTEM, and elevated sessions (unless
    /// <c>EnableLinkedConnections</c> is set), don't see the mapping.</summary>
    MappedNetwork,

    /// <summary>A <c>\\server\share</c> path.</summary>
    Unc,

    /// <summary>The drive letter isn't mounted at all.</summary>
    NoRootDir,
}

/// <summary>Whether a directory was actually looked at.</summary>
public enum ProbeStatus
{
    /// <summary>Read (the folder may still not exist; see <see cref="DirectoryFacts.Exists"/>).</summary>
    Probed,

    /// <summary>
    /// Not touched because reaching it means talking to another machine: a UNC path, a mapped drive, or a
    /// link whose target is one. Opening <c>\\host\share</c> authenticates to that host (an NTLM hash leak), so
    /// this needs the user's opt-in.
    /// </summary>
    SkippedNetwork,

    /// <summary>The probe hit an error it couldn't classify; see <see cref="DirectoryFacts.Note"/>.</summary>
    Failed,
}

/// <summary>
/// Everything captured about one directory: a PATH entry, the nearest existing ancestor of a missing one, or
/// a link target. Captured read-only: attributes, a handle opened for <c>READ_CONTROL</c>, never a write.
/// </summary>
public sealed record DirectoryFacts
{
    /// <summary>The cleaned, fully qualified path that was probed (the lookup key).</summary>
    public string Path { get; init; } = "";

    public ProbeStatus Status { get; init; }

    /// <summary>Why it was skipped or failed, or a caveat about what was read ("drive not ready").</summary>
    public string? Note { get; init; }

    public DriveKind Drive { get; init; }

    /// <summary>For a mapped drive, the share it maps to; for a <c>subst</c> drive, the folder it stands for.</summary>
    public string? DriveTarget { get; init; }

    public bool Exists { get; init; }

    public bool IsDirectory { get; init; }

    public FileAttributes Attributes { get; init; }

    /// <summary>The reparse tag, when <see cref="FileAttributes.ReparsePoint"/> is set.</summary>
    public uint? ReparseTag { get; init; }

    /// <summary>Where a junction or symlink points, read from its reparse data (never by following it).</summary>
    public string? ReparseTarget { get; init; }

    /// <summary>True when <see cref="ReparseTarget"/> leads to the network, so it was left alone.</summary>
    public bool ReparseTargetIsNetwork { get; init; }

    /// <summary>The long form of a path that contained 8.3 short names, or null.</summary>
    public string? LongPath { get; init; }

    /// <summary>The owner SID as a string.</summary>
    public string? OwnerSid { get; init; }

    /// <summary>Owner, group, DACL and integrity label as SDDL: exactly what <see cref="Access"/> was evaluated against.</summary>
    public string? Sddl { get; init; }

    /// <summary>Why the security descriptor couldn't be read (typically access denied), or null.</summary>
    public string? SecurityError { get; init; }

    /// <summary>For a missing directory, the closest parent that exists (the phantom-directory check needs it).</summary>
    public string? NearestExistingAncestor { get; init; }

    /// <summary>One result per perspective, when a security descriptor was captured.</summary>
    public IReadOnlyList<AccessResult> Access { get; init; } = [];

    [JsonIgnore] public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c>: a junction (or a volume mount point).</summary>
    [JsonIgnore] public bool IsJunction => ReparseTag == ReparseTags.MountPoint;

    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>.</summary>
    [JsonIgnore] public bool IsSymlink => ReparseTag == ReparseTags.Symlink;

    public AccessResult? AccessFor(Perspective perspective) => Access.FirstOrDefault(a => a.Perspective == perspective);
}

/// <summary>The reparse tags PATHology tells apart.</summary>
public static class ReparseTags
{
    public const uint MountPoint = 0xA0000003;
    public const uint Symlink = 0xA000000C;

    /// <summary>Set on tags whose target names another file or folder (junctions and symlinks both carry it).</summary>
    public const uint NameSurrogateBit = 0x20000000;

    public static bool IsNameSurrogate(uint tag) => (tag & NameSurrogateBit) != 0;
}
