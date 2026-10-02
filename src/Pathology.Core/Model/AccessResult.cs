using System.Text.Json.Serialization;

namespace Pathology.Core.Model;

/// <summary>
/// Directory access rights (<c>FILE_*</c> / standard / generic). A directory's <c>WriteData</c> bit is
/// <c>FILE_ADD_FILE</c> and its <c>AppendData</c> bit is <c>FILE_ADD_SUBDIRECTORY</c>.
/// </summary>
[Flags]
public enum FileAccessRights : uint
{
    None = 0,
    ListDirectory = 0x1,
    AddFile = 0x2,
    AddSubdirectory = 0x4,
    ReadExtendedAttributes = 0x8,
    WriteExtendedAttributes = 0x10,
    Traverse = 0x20,
    DeleteChild = 0x40,
    ReadAttributes = 0x80,
    WriteAttributes = 0x100,
    Delete = 0x10000,
    ReadControl = 0x20000,
    WriteDac = 0x40000,
    WriteOwner = 0x80000,
    Synchronize = 0x100000,
    GenericAll = 0x10000000,
    GenericExecute = 0x20000000,
    GenericWrite = 0x40000000,
    GenericRead = 0x80000000,
}

/// <summary>An allow ACE that contributes a write-class right to a perspective.</summary>
public sealed record GrantingAce
{
    /// <summary>The trustee SID (see <see cref="WellKnownSids.NameOf"/> for display).</summary>
    public string Sid { get; init; } = "";

    /// <summary>The ACE's mask as stored (generic bits included).</summary>
    public FileAccessRights Mask { get; init; }

    /// <summary>True when inherited from a parent, which points the fix at the parent (or the drive root).</summary>
    public bool Inherited { get; init; }
}

/// <summary>
/// What one <see cref="Perspective"/> may do to one directory, from an access check over the captured security
/// descriptor. Nothing is written to find this out.
/// </summary>
/// <remarks>
/// Privileges (backup, restore, take-ownership) aren't modelled. They only widen what SYSTEM and elevated
/// admins can already do, and those perspectives are the victims here, not the attackers.
/// </remarks>
public sealed record AccessResult
{
    public Perspective Perspective { get; init; }

    /// <summary>The <c>MAXIMUM_ALLOWED</c> mask the access check granted.</summary>
    public FileAccessRights Granted { get; init; }

    /// <summary>The owner SID is one of this perspective's enabled SIDs.</summary>
    public bool OwnerInPerspective { get; init; }

    /// <summary>
    /// <see cref="CanWriteDac"/> comes only from being the owner (no ACE grants it and no <c>OWNER RIGHTS</c> ACE
    /// overrides it). The "owned by a non-admin" detector explains this case.
    /// </summary>
    public bool WriteDacFromOwnership { get; init; }

    /// <summary>The allow ACEs behind the write-class rights in <see cref="Granted"/>.</summary>
    public IReadOnlyList<GrantingAce> GrantedBy { get; init; } = [];

    /// <summary>Why the check couldn't run, or null when it did.</summary>
    public string? Error { get; init; }

    [JsonIgnore] public bool Evaluated => Error is null;
    [JsonIgnore] public bool CanAddFiles => Granted.HasFlag(FileAccessRights.AddFile);
    [JsonIgnore] public bool CanAddSubdirectories => Granted.HasFlag(FileAccessRights.AddSubdirectory);
    [JsonIgnore] public bool CanWriteDac => Granted.HasFlag(FileAccessRights.WriteDac);
    [JsonIgnore] public bool CanWriteOwner => Granted.HasFlag(FileAccessRights.WriteOwner);

    /// <summary>Any route to planting a file: add one, add a folder, or rewrite the ACL or owner to allow it.</summary>
    [JsonIgnore] public bool CanPlant => CanAddFiles || CanAddSubdirectories || CanWriteDac || CanWriteOwner;

    /// <summary>The write-class bits the granting-ACE analysis looks for.</summary>
    public const FileAccessRights WriteClass =
        FileAccessRights.AddFile | FileAccessRights.AddSubdirectory | FileAccessRights.DeleteChild
        | FileAccessRights.WriteDac | FileAccessRights.WriteOwner;
}
