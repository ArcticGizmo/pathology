using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pathology.Core.Capture;
using Pathology.Core.Model;
using Pathology.Core.Normalisation;
using static Pathology.Windows.Native.NativeMethods;

namespace Pathology.Windows;

/// <summary>
/// Reads one directory's facts: attributes, reparse data, drive type, owner, DACL and label. Read-only: a
/// handle is opened for <c>READ_CONTROL | FILE_READ_ATTRIBUTES</c> and nothing is ever created or written.
/// </summary>
/// <remarks>
/// <para><b>The network is off limits unless <c>allowNetwork</c> is set.</b> A UNC path or mapped drive is
/// classified without being opened. So is anything reached <i>through</i> a link that leads there: before a
/// path is opened, each of its components is checked, and a junction or symlink is resolved by reading its
/// reparse data, never by following it. <c>C:\Tools</c> as a symlink to <c>\\attacker\share</c> is reported,
/// not visited.</para>
/// <para>Every probe runs with <c>SEM_FAILCRITICALERRORS</c>, so an empty card reader or DVD drive answers
/// "not ready" instead of popping "insert a disk".</para>
/// </remarks>
public sealed unsafe class DirectoryProbe : IDirectoryProbe
{
    const int MaxReparseBuffer = 16 * 1024;

    public DirectoryFacts Probe(string path, bool allowNetwork)
    {
        using var _ = new ErrorModeScope();
        return ProbeCore(path, allowNetwork);
    }

    DirectoryFacts ProbeCore(string path, bool allowNetwork)
    {
        var facts = new DirectoryFacts { Path = path };
        switch (PathText.Classify(path))
        {
            case PathForm.Unc:
                facts = facts with { Drive = DriveKind.Unc };
                if (!allowNetwork) return SkippedNetwork(facts, "A network path. It's classified from its text unless network probing is turned on.");
                break;

            case PathForm.Absolute:
            {
                var (drive, target) = DriveOf(path);
                facts = facts with { Drive = drive, DriveTarget = target };
                if (drive == DriveKind.NoRootDir)
                    return facts with { Status = ProbeStatus.Probed, Note = "The drive letter isn't mounted." };
                if (drive == DriveKind.MappedNetwork && !allowNetwork)
                    return SkippedNetwork(facts, "On a network drive. It's classified from its text unless network probing is turned on.");
                break;
            }

            case PathForm.DevicePath when path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase):
                break;

            default:
                return facts with { Status = ProbeStatus.Failed, Note = "Only fully qualified paths are probed." };
        }

        if (!allowNetwork && LinkToNetwork(path, includeLeaf: false, depth: 0) is { } via)
            return SkippedNetwork(facts, $"The path runs through {via}, which leads to the network or couldn't be inspected, so it wasn't followed.");

        var win32 = ForWin32(path);
        if (!GetFileAttributesEx(win32, 0, out var data))
        {
            var error = Marshal.GetLastPInvokeError();
            return error switch
            {
                ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_INVALID_NAME or ERROR_BAD_NETPATH or ERROR_BAD_NET_NAME
                    => facts with { Status = ProbeStatus.Probed },
                ERROR_NOT_READY or ERROR_INVALID_DRIVE
                    => facts with { Status = ProbeStatus.Probed, Note = "The drive isn't ready (no media?)." },
                _ => facts with { Status = ProbeStatus.Failed, Note = new Win32Exception(error).Message },
            };
        }

        var attributes = (FileAttributes)data.FileAttributes;
        facts = facts with
        {
            Status = ProbeStatus.Probed,
            Exists = true,
            IsDirectory = attributes.HasFlag(FileAttributes.Directory),
            Attributes = attributes,
            LongPath = path.Contains('~') ? LongPathOf(win32) : null,
        };

        // Both handles open the link as itself (OPEN_REPARSE_POINT), never its target. The security handle asks
        // for READ_CONTROL alone, which an owner always holds, so an owner-only folder (the "owned by a non-admin"
        // case) still yields its ACL. The link handle is a normal synchronous one, for the reparse data.
        using var security = OpenExact(path, READ_CONTROL, out var securityError);
        using var handle = Open(win32, FILE_READ_ATTRIBUTES, out _);

        if (security is null)
            facts = facts with
            {
                SecurityError = securityError == ERROR_ACCESS_DENIED
                    ? "Access to the security descriptor was denied."
                    : new Win32Exception(securityError).Message,
            };
        else
        {
            try
            {
                var (owner, sddl) = ReadSecurity(security);
                facts = facts with { OwnerSid = owner, Sddl = sddl };
            }
            catch (Win32Exception ex) { facts = facts with { SecurityError = ex.Message }; }
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint) && handle is null)
            facts = facts with { Note = "A link whose target couldn't be read." };
        if (handle is not null && attributes.HasFlag(FileAttributes.ReparsePoint) && TagOf(handle) is { } tag)
        {
            facts = facts with { ReparseTag = tag };
            if (ReparseTags.IsNameSurrogate(tag) && ReadReparseTarget(handle, path) is { } target)
            {
                facts = facts with
                {
                    ReparseTarget = target,
                    ReparseTargetIsNetwork = IsNetworkLocation(target) || LinkToNetwork(target, includeLeaf: true, depth: 1) is not null,
                };
            }
        }

        return facts;
    }

    /// <summary>Stop listing a folder after this many files: a PATH folder with more is a mistake, not a toolbox.</summary>
    public const int MaxListedFiles = 20_000;

    public IReadOnlyList<string>? ListFiles(string path, IReadOnlySet<string> extensions, bool allowNetwork)
    {
        using var _ = new ErrorModeScope();
        // Listing opens the folder and goes through a link at the leaf, so the leaf's target is checked too.
        if (!allowNetwork && (IsNetworkLocation(path) || LinkToNetwork(path, includeLeaf: true, depth: 0) is not null))
            return null;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,   // hidden and system files still run
            MatchType = MatchType.Win32,
            ReturnSpecialDirectories = false,
        };
        try
        {
            var names = new List<string>();
            foreach (var file in new DirectoryInfo(ForWin32(path)).EnumerateFiles("*", options))
            {
                if (!extensions.Contains(Path.GetExtension(file.Name))) continue;
                names.Add(file.Name);
                if (names.Count >= MaxListedFiles) break;
            }
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    static DirectoryFacts SkippedNetwork(DirectoryFacts facts, string note) =>
        facts with { Status = ProbeStatus.SkippedNetwork, Note = note };

    /// <summary>
    /// The first link on the way to <paramref name="path"/> whose target is on the network, or null when the
    /// whole route is local. Only local components are ever touched: a network root is recognised from its text
    /// or drive type, and each link's target is read from its reparse data before anything goes through it.
    /// </summary>
    static string? LinkToNetwork(string path, bool includeLeaf, int depth)
    {
        if (depth > SnapshotCapturer.MaxLinkHops) return path;
        if (IsNetworkLocation(path)) return path;
        if (PathText.RootOf(path) is not { } root) return path;

        // A local subst drive is walked as the folder it stands for, whose route may pass through a link itself.
        if (PathText.Classify(path) == PathForm.Absolute && DriveOf(path).Target is { } substituted)
            return LinkToNetwork(substituted.TrimEnd('\\') + path[2..], includeLeaf, depth + 1) is null ? null : root;

        var segments = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var prefix = root.TrimEnd('\\');
        for (var i = 0; i < segments.Length; i++)
        {
            prefix += @"\" + segments[i];
            if (i == segments.Length - 1 && !includeLeaf) break;

            // A link we can't inspect might lead anywhere, so the route stops there. Only "doesn't exist" (or no
            // media) means nothing further can be reached; any other failure leaves this component unread.
            if (!GetFileAttributesEx(ForWin32(prefix), 0, out var data))
                return Marshal.GetLastPInvokeError() is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_INVALID_NAME
                    or ERROR_NOT_READY or ERROR_INVALID_DRIVE ? null : prefix;
            if (((FileAttributes)data.FileAttributes & FileAttributes.ReparsePoint) == 0) continue;

            using var link = Open(ForWin32(prefix), FILE_READ_ATTRIBUTES, out _);
            if (link is null) return prefix;
            if (TagOf(link) is not { } tag) return prefix;
            if (!ReparseTags.IsNameSurrogate(tag)) continue;
            if (ReadReparseTarget(link, prefix) is not { } target) return prefix;
            if (LinkToNetwork(target, includeLeaf: true, depth + 1) is not null) return prefix;
        }
        return null;
    }

    /// <summary>True for a UNC path, a mapped network drive, or a drive <c>subst</c>-ed onto a share.</summary>
    static bool IsNetworkLocation(string path) => PathText.Classify(path) switch
    {
        PathForm.Unc => true,
        PathForm.Absolute => DriveOf(path).Kind == DriveKind.MappedNetwork,
        PathForm.DevicePath => !path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase),
        _ => true,   // anything unrecognised is treated as unsafe to go through
    };

    static (DriveKind Kind, string? Target) DriveOf(string path)
    {
        var letter = path[..2];
        var kind = GetDriveType(letter + @"\") switch
        {
            1 => DriveKind.NoRootDir,
            2 => DriveKind.Removable,
            3 => DriveKind.Fixed,
            4 => DriveKind.MappedNetwork,
            5 => DriveKind.CdRom,
            6 => DriveKind.RamDisk,
            _ => DriveKind.Unknown,
        };

        if (kind == DriveKind.MappedNetwork)
        {
            var buffer = stackalloc char[1024];
            uint length = 1024;
            return (kind, WNetGetConnection(letter, buffer, ref length) == 0 ? new string(buffer) : null);
        }

        // A subst drive's DOS device reads "\??\C:\folder" (or "\??\UNC\host\share").
        var target = stackalloc char[1024];
        if (QueryDosDevice(letter, target, 1024) > 0 && new string(target) is var device && device.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            var substituted = FromNtPath(device);
            return (PathText.IsUnc(substituted) ? DriveKind.MappedNetwork : kind, substituted);
        }
        return (kind, null);
    }

    static SafeFileHandle? Open(string win32Path, uint access, out int error)
    {
        var handle = CreateFile(win32Path, access, FILE_SHARE_ALL, 0, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>
    /// <c>NtOpenFile</c> for exactly <paramref name="access"/> (no implied <c>SYNCHRONIZE</c>), opening a link as
    /// itself. The handle is asynchronous, so it's only good for queries such as <c>GetSecurityInfo</c>.
    /// </summary>
    internal static SafeFileHandle? OpenExact(string canonicalPath, uint access, out int error)
    {
        var nt = PathText.Classify(canonicalPath) switch
        {
            PathForm.Unc => @"\??\UNC\" + canonicalPath[2..],
            PathForm.DevicePath => @"\??\" + canonicalPath[4..],
            _ => @"\??\" + canonicalPath,
        };
        fixed (char* chars = nt)
        {
            var name = new UNICODE_STRING { Length = (ushort)(nt.Length * 2), MaximumLength = (ushort)(nt.Length * 2), Buffer = chars };
            var attributes = new OBJECT_ATTRIBUTES { Length = sizeof(OBJECT_ATTRIBUTES), ObjectName = &name, Attributes = OBJ_CASE_INSENSITIVE };
            IO_STATUS_BLOCK io;
            var status = NtOpenFile(out var handle, access, &attributes, &io, FILE_SHARE_ALL, FILE_OPEN_FOR_BACKUP_INTENT | FILE_OPEN_REPARSE_POINT);
            error = status == 0 ? 0 : (int)RtlNtStatusToDosError(status);
            if (status == 0) return handle;
            handle.Dispose();
            return null;
        }
    }

    internal static uint? TagOf(SafeFileHandle handle)
    {
        FILE_ATTRIBUTE_TAG_INFO info;
        return GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO))
               && (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0
            ? info.ReparseTag
            : null;
    }

    /// <summary>Owner SID and SDDL (owner, group, DACL, label) from an open handle.</summary>
    internal static (string Owner, string Sddl) ReadSecurity(SafeFileHandle handle)
    {
        const uint info = OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION;
        nint owner, descriptor;
        var status = GetSecurityInfo(handle, SE_FILE_OBJECT, info, &owner, null, null, null, &descriptor);
        if (status != 0) throw new Win32Exception((int)status);
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, SDDL_REVISION_1, info, out var text, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            try { return (SidToString(owner), Marshal.PtrToStringUni(text)!); }
            finally { LocalFree(text); }
        }
        finally { LocalFree(descriptor); }
    }

    /// <summary>
    /// Where a junction or symlink points, from <c>FSCTL_GET_REPARSE_POINT</c> (which reads the link; it doesn't
    /// follow it). Relative symlinks are resolved against the link's folder. Null for other reparse tags.
    /// </summary>
    static string? ReadReparseTarget(SafeFileHandle handle, string linkPath)
    {
        var buffer = (byte*)NativeMemory.Alloc(MaxReparseBuffer);
        try
        {
            if (!DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT, null, 0, buffer, MaxReparseBuffer, out _, 0)) return null;

            var tag = *(uint*)buffer;
            byte* names;
            var relative = false;
            if (tag == ReparseTags.MountPoint) names = buffer + 16;
            else if (tag == ReparseTags.Symlink) { relative = (*(uint*)(buffer + 16) & 1) != 0; names = buffer + 20; }
            else return null;

            var substitute = new string((char*)(names + *(ushort*)(buffer + 8)), 0, *(ushort*)(buffer + 10) / 2);
            var print = new string((char*)(names + *(ushort*)(buffer + 12)), 0, *(ushort*)(buffer + 14) / 2);
            var name = substitute.Length > 0 ? substitute : print;

            // A relative target is relative to the link's folder, or to its drive root when it starts with "\".
            var resolved = !relative ? FromNtPath(name)
                : name.StartsWith('\\') && PathText.RootOf(linkPath) is { } root ? root.TrimEnd('\\') + name
                : PathText.Parent(linkPath) is { } folder ? folder.TrimEnd('\\') + @"\" + name
                : name;
            return PathText.Canonical(PathText.Strip(resolved)) ?? resolved;
        }
        finally { NativeMemory.Free(buffer); }
    }

    /// <summary><c>\??\C:\x</c> → <c>C:\x</c>, <c>\??\UNC\h\s</c> → <c>\\h\s</c>, <c>\??\Volume{…}</c> → <c>\\?\Volume{…}</c>.</summary>
    static string FromNtPath(string nt)
    {
        if (!nt.StartsWith(@"\??\", StringComparison.Ordinal)) return nt;
        var rest = nt[4..];
        if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + rest[4..];
        return rest.Length >= 2 && rest[1] == ':' ? rest : @"\\?\" + rest;
    }

    static string? LongPathOf(string win32Path)
    {
        var needed = GetLongPathName(win32Path, null, 0);
        if (needed == 0) return null;
        var buffer = new char[needed];
        fixed (char* p = buffer)
        {
            var written = GetLongPathName(win32Path, p, needed);
            if (written == 0 || written >= needed) return null;
            var text = new string(p, 0, (int)written);
            return PathText.Canonical(text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text);
        }
    }

    /// <summary>A long path gets the <c>\\?\</c> prefix so Win32 doesn't stop at <c>MAX_PATH</c> (it's already canonical).</summary>
    static string ForWin32(string path)
    {
        if (path.Length < 248) return path;
        return PathText.Classify(path) switch
        {
            PathForm.Absolute => @"\\?\" + path,
            PathForm.Unc => @"\\?\UNC\" + path[2..],
            _ => path,
        };
    }

    /// <summary>Suppresses the critical-error dialog ("insert a disk") on this thread for the probe's duration.</summary>
    readonly ref struct ErrorModeScope
    {
        readonly uint _previous;
        readonly bool _set;

        public ErrorModeScope() => _set = SetThreadErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX, out _previous);

        public void Dispose()
        {
            if (_set) SetThreadErrorMode(_previous, out _);
        }
    }
}
