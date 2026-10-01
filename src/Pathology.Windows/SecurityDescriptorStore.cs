using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Pathology.Core.Model;
using Pathology.Core.Normalisation;
using Pathology.Core.Remediation;
using static Pathology.Windows.Native.NativeMethods;

namespace Pathology.Windows;

/// <summary>
/// Reads and writes a folder's owner and DACL. <b>A writer</b>: built only by <c>AppServices</c> and the elevated
/// helper's verb, and called only from an apply the user clicked (see CLAUDE.md). Tests use it on folders a
/// <c>TempTree</c> created, nothing else.
/// </summary>
/// <remarks>
/// <para>Only plain local folders are touched: a fully qualified path on a fixed drive (not a subst drive), with
/// no junction or symlink anywhere on the way to it. Each component is checked, root first, before anything is
/// opened, so nothing is ever followed to somewhere else (or to the network).</para>
/// <para>The write goes through one handle, opened with <c>FILE_OPEN_REPARSE_POINT</c>: if the folder were swapped
/// for a link after the check, the handle would be the link itself, which is refused, never its target. With
/// inheritance on, only the folder's own entries are written and Windows recomputes the inherited ones; with it off,
/// the DACL is written whole. Either way <c>SetSecurityInfo</c> carries inheritable entries down to what's inside,
/// as icacls does.</para>
/// </remarks>
public sealed unsafe class SecurityDescriptorStore : IAclStore
{
    const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x20;
    const uint SYNCHRONIZE = 0x100000;

    public string Read(string path)
    {
        Guard(path);
        using var handle = Open(path, READ_CONTROL) ?? throw new Win32Exception(_lastError);
        return DirectoryProbe.ReadSecurity(handle).Sddl;
    }

    public bool SameOwnPermissions(string expected, string actual)
    {
        try
        {
            var a = new RawSecurityDescriptor(expected);
            var b = new RawSecurityDescriptor(actual);
            return a.Owner == b.Owner
                   && a.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) == b.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
                   && OwnEntries(a).SequenceEqual(OwnEntries(b), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    public void Write(string path, string sddl)
    {
        Guard(path);
        var target = new RawSecurityDescriptor(sddl);
        var protect = target.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected);

        var access = READ_CONTROL | WRITE_DAC | FILE_READ_ATTRIBUTES | SYNCHRONIZE;
        using var probe = Open(path, READ_CONTROL) ?? throw new Win32Exception(_lastError);
        var current = new RawSecurityDescriptor(DirectoryProbe.ReadSecurity(probe).Sddl);
        var ownerChanges = target.Owner is not null && target.Owner != current.Owner;
        if (ownerChanges) access |= WRITE_OWNER;

        using var handle = Open(path, access, FILE_SYNCHRONOUS_IO_NONALERT) ?? throw new Win32Exception(_lastError);
        if (DirectoryProbe.TagOf(handle) is not null)
            throw new InvalidOperationException($"{path} turned into a link, so its permissions were left alone.");

        // With inheritance on, Windows works out the inherited entries itself; only the folder's own are passed.
        var dacl = new RawAcl(GenericAcl.AclRevision, target.DiscretionaryAcl?.Count ?? 0);
        foreach (var ace in target.DiscretionaryAcl?.Cast<GenericAce>() ?? [])
            if (protect || !ace.IsInherited) dacl.InsertAce(dacl.Count, ace);

        var daclBytes = new byte[dacl.BinaryLength];
        dacl.GetBinaryForm(daclBytes, 0);
        var ownerBytes = ownerChanges ? new byte[target.Owner!.BinaryLength] : null;
        if (ownerBytes is not null) target.Owner!.GetBinaryForm(ownerBytes, 0);

        var info = DACL_SECURITY_INFORMATION | (protect ? PROTECTED_DACL_SECURITY_INFORMATION : UNPROTECTED_DACL_SECURITY_INFORMATION)
                   | (ownerChanges ? OWNER_SECURITY_INFORMATION : 0);
        fixed (byte* d = daclBytes)
        fixed (byte* o = ownerBytes)
        {
            var status = SetSecurityInfo(handle, SE_FILE_OBJECT, info, o, null, d, null);
            if (status != 0) throw new Win32Exception((int)status);
        }

        var written = DirectoryProbe.ReadSecurity(handle).Sddl;
        if (!SameOwnPermissions(sddl, written))
            throw new InvalidOperationException($"The permissions on {path} were written, but didn't read back as planned.");
    }

    /// <summary>The folder's own entries, inherited ones left out, in a comparable form.</summary>
    static IEnumerable<string> OwnEntries(RawSecurityDescriptor descriptor) =>
        (descriptor.DiscretionaryAcl?.Cast<GenericAce>() ?? [])
        .Where(a => !a.IsInherited)
        .Select(a => a is CommonAce c
            ? $"{c.AceQualifier}|{c.AceFlags & ~AceFlags.Inherited}|{c.AccessMask:X}|{c.SecurityIdentifier.Value}"
            : a.ToString() ?? "")
        .Order(StringComparer.Ordinal);

    /// <summary>
    /// Refuse anything but a plain, existing local folder: fully qualified, on a fixed drive that isn't a subst
    /// drive, with no link on the way to it or at it. Components are checked from the root down, so a link is
    /// noticed before anything goes through it.
    /// </summary>
    static void Guard(string path)
    {
        if (PathText.Classify(path) != PathForm.Absolute || PathText.Canonical(path) != path)
            throw new InvalidOperationException($"{path} isn't a plain local path, so its permissions weren't touched.");

        var root = PathText.RootOf(path)!;
        if (GetDriveType(root) != 3)
            throw new InvalidOperationException($"{path} isn't on a fixed local drive, so its permissions weren't touched.");
        var device = stackalloc char[1024];
        if (QueryDosDevice(root.TrimEnd('\\'), device, 1024) > 0 && new string(device).StartsWith(@"\??\", StringComparison.Ordinal))
            throw new InvalidOperationException($"{root} is a subst drive, so {path}'s permissions weren't touched.");

        var prefix = root.TrimEnd('\\');
        foreach (var segment in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            prefix += @"\" + segment;
            var win32 = prefix.Length < 248 ? prefix : @"\\?\" + prefix;
            if (!GetFileAttributesEx(win32, 0, out var data)) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{prefix} can't be read");
            if (((FileAttributes)data.FileAttributes).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"{prefix} is a junction or symlink, so {path}'s permissions weren't touched. Lock down where it points instead.");
            if (prefix.Length == path.Length && !((FileAttributes)data.FileAttributes).HasFlag(FileAttributes.Directory))
                throw new InvalidOperationException($"{path} is a file, not a folder.");
        }
    }

    [ThreadStatic] static int _lastError;

    /// <summary><c>NtOpenFile</c> for exactly the rights named, as the link itself, with backup intent (which the
    /// elevated helper's restore privilege honours).</summary>
    static SafeFileHandle? Open(string path, uint access, uint extraOptions = 0)
    {
        var nt = @"\??\" + path;
        fixed (char* chars = nt)
        {
            var name = new UNICODE_STRING { Length = (ushort)(nt.Length * 2), MaximumLength = (ushort)(nt.Length * 2), Buffer = chars };
            var attributes = new OBJECT_ATTRIBUTES { Length = sizeof(OBJECT_ATTRIBUTES), ObjectName = &name, Attributes = OBJ_CASE_INSENSITIVE };
            IO_STATUS_BLOCK io;
            var status = NtOpenFile(out var handle, access, &attributes, &io, FILE_SHARE_ALL,
                FILE_OPEN_FOR_BACKUP_INTENT | FILE_OPEN_REPARSE_POINT | extraOptions);
            _lastError = status == 0 ? 0 : (int)RtlNtStatusToDosError(status);
            if (status == 0) return handle;
            handle.Dispose();
            return null;
        }
    }
}

/// <summary>Whether this process is elevated, and the privileges the elevated helper turns on.</summary>
public static class Privileges
{
    /// <summary>True when this process holds an enabled Administrators group (it's elevated).</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Turn on restore and take-ownership in the helper's own token, so a folder whose DACL leaves administrators
    /// out (the owned-by-someone-else case) can still be locked down. Best effort: a privilege the token lacks is
    /// skipped.
    /// </summary>
    public static unsafe void EnableForRepair()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return;
        using (token)
        {
            foreach (var name in new[] { "SeRestorePrivilege", "SeTakeOwnershipPrivilege", "SeBackupPrivilege" })
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) continue;
                var state = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED } };
                AdjustTokenPrivileges(token, false, &state, (uint)sizeof(TOKEN_PRIVILEGES), 0, 0);
            }
        }
    }
}
