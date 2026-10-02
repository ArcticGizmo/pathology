using Pathology.Core.Model;

namespace Pathology.Core.Detection;

/// <summary>Reads the captured <see cref="AccessResult"/>s the way the security detectors ask about them.</summary>
public static class Writability
{
    /// <summary>
    /// Can drop a file straight into the folder: add a file, or rewrite the ACL or owner to allow it. Set
    /// <paramref name="countOwnership"/> to false to leave out a <c>WRITE_DAC</c> that comes only from owning
    /// the folder (the owner detector reports that case on its own).
    /// </summary>
    public static bool CanPlantFiles(AccessResult? access, bool countOwnership = true) =>
        access is { Evaluated: true }
        && (access.CanAddFiles || access.CanWriteOwner || (access.CanWriteDac && (countOwnership || !access.WriteDacFromOwnership)));

    /// <summary>Can create a subfolder (the phantom-directory route): add one, or rewrite the ACL or owner.</summary>
    public static bool CanCreateFolders(AccessResult? access) =>
        access is { Evaluated: true } && (access.CanAddSubdirectories || access.CanWriteDac || access.CanWriteOwner);

    /// <summary>
    /// Owners whose implicit <c>WRITE_DAC</c> is no threat: Administrators, SYSTEM, TrustedInstaller, service
    /// SIDs, and the well-known admin RIDs of a machine or domain (500, 512, 519).
    /// </summary>
    public static bool IsAdministrativeOwner(string? sid)
    {
        if (sid is null) return false;
        if (sid is WellKnownSids.Administrators or WellKnownSids.LocalSystem or WellKnownSids.TrustedInstaller) return true;
        if (sid.StartsWith("S-1-5-80-", StringComparison.Ordinal)) return true;
        return sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)
               && (sid.EndsWith("-500", StringComparison.Ordinal) || sid.EndsWith("-512", StringComparison.Ordinal) || sid.EndsWith("-519", StringComparison.Ordinal));
    }

    /// <summary>"Authenticated Users: modify (inherited)". <paramref name="userSid"/>, when given, is shown as "you".</summary>
    public static string Describe(GrantingAce ace, string? userSid = null) =>
        $"{NameOf(ace.Sid, userSid)}: {Rights(ace.Mask)}{(ace.Inherited ? " (inherited)" : "")}";

    static string NameOf(string sid, string? userSid) =>
        userSid is not null && string.Equals(sid, userSid, StringComparison.OrdinalIgnoreCase) ? "you" : WellKnownSids.NameOf(sid);

    /// <summary>The write-class rights in a mask, in words.</summary>
    public static string Rights(FileAccessRights mask)
    {
        var m = (uint)mask;
        if ((m & (uint)FileAccessRights.GenericAll) != 0 || (m & 0x1F01FF) == 0x1F01FF) return "full control";
        // Generic bits stand for the directory rights they map to (FILE_GENERIC_READ / _WRITE / _EXECUTE).
        if ((m & (uint)FileAccessRights.GenericRead) != 0) m |= 0x120089;
        if ((m & (uint)FileAccessRights.GenericWrite) != 0) m |= 0x120116;
        if ((m & (uint)FileAccessRights.GenericExecute) != 0) m |= 0x1200A0;
        if ((m & 0x1301BF) == 0x1301BF) return "modify";

        var words = new List<string>();
        if ((m & (uint)FileAccessRights.AddFile) != 0) words.Add("create files");
        if ((m & (uint)FileAccessRights.AddSubdirectory) != 0) words.Add("create folders");
        if ((m & (uint)FileAccessRights.DeleteChild) != 0) words.Add("delete contents");
        if ((m & (uint)FileAccessRights.WriteDac) != 0) words.Add("change permissions");
        if ((m & (uint)FileAccessRights.WriteOwner) != 0) words.Add("take ownership");
        return words.Count == 0 ? "read" : string.Join(", ", words);
    }

    /// <summary>One evidence line per ACE behind a perspective's write access, or a note when it's ownership.</summary>
    public static IEnumerable<string> EvidenceFor(AccessResult? access, string who, string? userSid = null)
    {
        if (access is null) yield break;
        foreach (var ace in access.GrantedBy)
            yield return $"{who} can write it through: {Describe(ace, userSid)}";
        if (access.WriteDacFromOwnership)
            yield return $"{who} owns it, so can change its permissions whatever they say";
    }

    /// <summary>A folder's owner, in words.</summary>
    public static string OwnerName(DirectoryFacts facts, DetectionContext context) =>
        facts.OwnerSid is not { } sid ? "an unknown owner"
        : string.Equals(sid, context.Snapshot.Host.UserSid, StringComparison.OrdinalIgnoreCase) ? "you"
        : WellKnownSids.NameOf(sid);
}
