using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Pathology.Core.Normalisation;

namespace Pathology.Windows.Tests;

/// <summary>
/// A throwaway directory tree under the temp folder: the only place these tests create folders, set ACLs or
/// make links. Deleted on dispose (links are removed, never followed).
/// </summary>
public sealed unsafe class TempTree : IDisposable
{
    public TempTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "pathology-wintests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Canonical and long-named (the temp path can be 8.3), so paths compare equal to what the probe reports.
        Root = PathText.Canonical(LongName(root))!;
    }

    public string Root { get; }

    public static string CurrentUserSid => WindowsIdentity.GetCurrent().User!.Value;

    public string Folder(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string PathOf(string relative) => Path.Combine(Root, relative);

    /// <summary>
    /// Replace a folder's DACL (and optionally owner) with <paramref name="sddl"/>, e.g. <c>D:PAI(A;;FA;;;SY)</c>.
    /// Only ever called on folders this tree created.
    /// </summary>
    public void SetAcl(string path, string sddl)
    {
        AssertInside(path);
        var security = new DirectorySecurity();
        var sections = AccessControlSections.Access;
        if (sddl.Contains("O:")) sections |= AccessControlSections.Owner;
        security.SetSecurityDescriptorSddlForm(sddl, sections);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    /// <summary>Make <paramref name="link"/> (an empty folder this tree creates) a junction to <paramref name="target"/>.</summary>
    public void Junction(string link, string target)
    {
        AssertInside(link);
        Directory.CreateDirectory(link);

        var substitute = @"\??\" + target;
        var substituteBytes = substitute.Length * 2;
        var printBytes = target.Length * 2;
        var pathBuffer = substituteBytes + 2 + printBytes + 2;
        var total = 16 + pathBuffer;

        var buffer = new byte[total];
        fixed (byte* b = buffer)
        {
            *(uint*)b = 0xA0000003;                            // IO_REPARSE_TAG_MOUNT_POINT
            *(ushort*)(b + 4) = (ushort)(8 + pathBuffer);      // ReparseDataLength
            *(ushort*)(b + 8) = 0;                             // SubstituteNameOffset
            *(ushort*)(b + 10) = (ushort)substituteBytes;
            *(ushort*)(b + 12) = (ushort)(substituteBytes + 2); // PrintNameOffset
            *(ushort*)(b + 14) = (ushort)printBytes;
            fixed (char* s = substitute) Buffer.MemoryCopy(s, b + 16, substituteBytes, substituteBytes);
            fixed (char* p = target) Buffer.MemoryCopy(p, b + 16 + substituteBytes + 2, printBytes, printBytes);

            using var handle = CreateFile(link, 0x40000000 /* GENERIC_WRITE */, 0, 0, 3 /* OPEN_EXISTING */,
                0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, 0);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
            if (!DeviceIoControl(handle, 0x000900A4 /* FSCTL_SET_REPARSE_POINT */, b, (uint)total, null, 0, out _, 0))
                throw new System.ComponentModel.Win32Exception();
        }
    }

    /// <summary>
    /// Make a directory symlink, if this machine allows it unelevated (Developer Mode). Returns false when it
    /// doesn't, so the caller can skip.
    /// </summary>
    public bool TrySymlink(string link, string target)
    {
        AssertInside(link);
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        try
        {
            // Give ourselves full control back first, top-down, in case a test locked a folder down.
            Restore(new DirectoryInfo(Root));
            Directory.Delete(Root, recursive: true);
        }
        catch { /* a leftover temp dir isn't a test failure */ }
    }

    static void Restore(DirectoryInfo dir)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm($"D:(A;OICI;FA;;;{CurrentUserSid})", AccessControlSections.Access);
            dir.SetAccessControl(security);
            foreach (var child in dir.EnumerateDirectories())
                if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint)) Restore(child);
        }
        catch { /* best effort */ }
    }

    void AssertInside(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Root + @"\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TempTree only touches folders inside its own root.");
    }

    static string LongName(string path)
    {
        var buffer = new char[32768];
        fixed (char* p = buffer)
        {
            var n = GetLongPathNameW(path, p, (uint)buffer.Length);
            return n == 0 ? path : new string(p, 0, (int)n);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetLongPathNameW(string shortPath, char* longPath, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, uint inSize, void* outBuffer, uint outSize, out uint returned, nint overlapped);
}
