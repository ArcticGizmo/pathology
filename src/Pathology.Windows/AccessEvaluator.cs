using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using Pathology.Core.Capture;
using Pathology.Core.Model;
using static Pathology.Windows.Native.NativeMethods;

namespace Pathology.Windows;

/// <summary>
/// Answers "what may this perspective do to this folder?" with <c>AuthzAccessCheck</c> over a captured
/// security descriptor. It's the kernel's own algorithm (deny ordering, inheritance, <c>OWNER RIGHTS</c>,
/// owner-implied rights), plus the integrity label's no-write-up rule applied on top, and nothing is written
/// to find out.
/// </summary>
/// <remarks>
/// The client context is built from the perspective's SIDs with <c>AUTHZ_SKIP_TOKEN_GROUPS</c>, so AuthZ never
/// looks the account up (that could reach a domain controller) and a synthetic SID set works as well as a real
/// one. Privileges aren't part of the context; see <see cref="AccessResult"/>.
/// </remarks>
public sealed unsafe class AccessEvaluator : IAccessEvaluator, IDisposable
{
    // The generic rights, mapped onto a directory's specific ones (FILE_GENERIC_* / FILE_ALL_ACCESS).
    const uint FileAllAccess = 0x1F01FF;
    const uint FileGenericRead = 0x120089;
    const uint FileGenericWrite = 0x120116;
    const uint FileGenericExecute = 0x1200A0;

    readonly Lock _gate = new();
    nint _resourceManager;

    public AccessResult Evaluate(string sddl, PerspectiveIdentity identity)
    {
        var granted = ApplyIntegrityLabel((FileAccessRights)Check(sddl, identity), sddl, identity);
        var descriptor = new RawSecurityDescriptor(sddl);
        var allow = new HashSet<string>(identity.AllowSids, StringComparer.OrdinalIgnoreCase);
        var owner = descriptor.Owner?.Value;
        var ownerIn = owner is not null && allow.Contains(owner);

        var grantedBy = GrantingAces(descriptor, allow, ownerIn, granted & AccessResult.WriteClass);
        var dacFromAce = grantedBy.Any(a => (Map(a.Mask) & FileAccessRights.WriteDac) != 0);

        return new AccessResult
        {
            Perspective = identity.Perspective,
            Granted = granted,
            OwnerInPerspective = ownerIn,
            WriteDacFromOwnership = ownerIn && granted.HasFlag(FileAccessRights.WriteDac) && !dacFromAce,
            GrantedBy = grantedBy,
        };
    }

    /// <summary>The <c>MAXIMUM_ALLOWED</c> mask for the perspective.</summary>
    uint Check(string sddl, PerspectiveIdentity identity)
    {
        var sids = new List<nint>();
        nint descriptor = 0, context = 0, withGroups = 0;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SDDL_REVISION_1, out descriptor, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The captured SDDL couldn't be parsed");

            var user = Sid(identity.UserSid, sids);
            if (!AuthzInitializeContextFromSid(AUTHZ_SKIP_TOKEN_GROUPS, user, ResourceManager, 0, default, 0, out context))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "AuthzInitializeContextFromSid failed");

            var groups = identity.Groups
                .Select(g => new SID_AND_ATTRIBUTES { Sid = Sid(g.Sid, sids), Attributes = (uint)(g.Attributes & ~GroupAttributes.LogonId) })
                .ToArray();
            fixed (SID_AND_ATTRIBUTES* p = groups)
                if (!AuthzAddSidsToContext(context, groups.Length == 0 ? null : p, (uint)groups.Length, null, 0, out withGroups))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "AuthzAddSidsToContext failed");

            var request = new AUTHZ_ACCESS_REQUEST { DesiredAccess = MAXIMUM_ALLOWED };
            uint mask = 0, error = 0;
            var reply = new AUTHZ_ACCESS_REPLY { ResultListLength = 1, GrantedAccessMask = &mask, Error = &error };
            if (!AuthzAccessCheck(0, withGroups, &request, 0, descriptor, null, 0, &reply, null))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "AuthzAccessCheck failed");

            // A reply error of ERROR_ACCESS_DENIED just means nothing was granted.
            return error is 0 or (uint)ERROR_ACCESS_DENIED ? mask : throw new Win32Exception((int)error);
        }
        finally
        {
            if (withGroups != 0) AuthzFreeContext(withGroups);
            if (context != 0) AuthzFreeContext(context);
            if (descriptor != 0) LocalFree(descriptor);
            foreach (var sid in sids) LocalFree(sid);
        }
    }

    /// <summary>
    /// Mandatory Integrity Control's no-write-up rule. AuthZ doesn't apply it to a context built from SIDs, so
    /// it's applied here: when the folder's label (<c>S:(ML;;NW;;;HI)</c>) is above the perspective's integrity
    /// level, the write rights are withdrawn whatever the DACL says. A folder with no label counts as Medium,
    /// which never restricts the Medium-or-higher perspectives PATHology evaluates.
    /// </summary>
    static FileAccessRights ApplyIntegrityLabel(FileAccessRights granted, string sddl, PerspectiveIdentity identity)
    {
        const FileAccessRights writes =
            FileAccessRights.AddFile | FileAccessRights.AddSubdirectory | FileAccessRights.WriteExtendedAttributes
            | FileAccessRights.WriteAttributes | FileAccessRights.Delete | FileAccessRights.WriteDac | FileAccessRights.WriteOwner;

        var own = identity.Groups.FirstOrDefault(g => (g.Attributes & GroupAttributes.Integrity) != 0);
        if (own is null || IntegrityRid(own.Sid) is not { } level) return granted;

        foreach (Match ace in LabelAce.Matches(sddl))
        {
            var flags = ace.Groups[1].Value;
            if (flags.Contains("IO", StringComparison.OrdinalIgnoreCase)) continue;   // applies to children only
            if (!NoWriteUp(ace.Groups[2].Value)) continue;
            if (IntegrityRid(ace.Groups[3].Value) is { } label && level < label) return granted & ~writes;
        }
        return granted;
    }

    static readonly Regex LabelAce = new(@"\(ML;([^;]*);([^;]*);;;([^)]*)\)", RegexOptions.CultureInvariant);

    static bool NoWriteUp(string rights) =>
        rights.Contains("NW", StringComparison.OrdinalIgnoreCase)
        || (rights.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(rights.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var mask) && (mask & 1) != 0);

    /// <summary>The level of an integrity SID or its SDDL alias (<c>ME</c>, <c>HI</c>, …).</summary>
    static uint? IntegrityRid(string sid) => sid.ToUpperInvariant() switch
    {
        "LW" => 0x1000,
        "ME" => 0x2000,
        "MP" => 0x2100,
        "HI" => 0x3000,
        "SI" => 0x4000,
        var s when s.StartsWith("S-1-16-", StringComparison.Ordinal) && uint.TryParse(s.AsSpan(7), out var rid) => rid,
        _ => null,
    };

    /// <summary>
    /// The allow ACEs that apply to the folder itself (not inherit-only), name one of the perspective's enabled
    /// SIDs (or <c>OWNER RIGHTS</c>, for the owner), and carry a write-class right the check actually granted.
    /// </summary>
    static List<GrantingAce> GrantingAces(
        RawSecurityDescriptor descriptor, HashSet<string> allow, bool ownerIn, FileAccessRights grantedWrites)
    {
        var result = new List<GrantingAce>();
        if (grantedWrites == 0 || descriptor.DiscretionaryAcl is not { } dacl) return result;

        foreach (var ace in dacl)
        {
            if (ace is not CommonAce { AceQualifier: AceQualifier.AccessAllowed } allowed) continue;
            if ((allowed.AceFlags & AceFlags.InheritOnly) != 0) continue;

            var sid = allowed.SecurityIdentifier.Value;
            var applies = allow.Contains(sid) || (ownerIn && sid == WellKnownSids.OwnerRights);
            var mask = (FileAccessRights)(uint)allowed.AccessMask;
            if (applies && (Map(mask) & grantedWrites) != 0)
                result.Add(new GrantingAce { Sid = sid, Mask = mask, Inherited = allowed.IsInherited });
        }
        return result;
    }

    /// <summary>Generic bits folded into the directory rights they stand for.</summary>
    static FileAccessRights Map(FileAccessRights mask)
    {
        var m = (uint)mask;
        if ((m & (uint)FileAccessRights.GenericAll) != 0) m |= FileAllAccess;
        if ((m & (uint)FileAccessRights.GenericRead) != 0) m |= FileGenericRead;
        if ((m & (uint)FileAccessRights.GenericWrite) != 0) m |= FileGenericWrite;
        if ((m & (uint)FileAccessRights.GenericExecute) != 0) m |= FileGenericExecute;
        return (FileAccessRights)(m & 0x00FFFFFF);
    }

    static nint Sid(string text, List<nint> owned)
    {
        if (!ConvertStringSidToSid(text, out var sid))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Not a valid SID: {text}");
        owned.Add(sid);
        return sid;
    }

    nint ResourceManager
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_resourceManager == -1, this);
                if (_resourceManager == 0
                    && !AuthzInitializeResourceManager(AUTHZ_RM_FLAG_NO_AUDIT, 0, 0, 0, "PATHology", out _resourceManager))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "AuthzInitializeResourceManager failed");
                return _resourceManager;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_resourceManager is not 0 and not -1) AuthzFreeResourceManager(_resourceManager);
            _resourceManager = -1;
        }
    }
}
