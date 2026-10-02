using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pathology.Windows.Native;

/// <summary>
/// The Win32 calls that <b>change</b> something: a folder's security descriptor, the process's privileges, and
/// the settings-changed broadcast. They're kept apart from the read-only surface in <c>NativeMethods.cs</c>, and
/// only the writers (<see cref="SecurityDescriptorStore"/>, <see cref="Privileges"/>, <see cref="EnvironmentBroadcast"/>)
/// call them. See CLAUDE.md: nothing writes an ACL except production code driven by a real click.
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const uint WRITE_DAC = 0x40000;
    public const uint WRITE_OWNER = 0x80000;
    public const uint PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;
    public const uint UNPROTECTED_DACL_SECURITY_INFORMATION = 0x20000000;

    [LibraryImport("advapi32.dll")]
    public static partial uint SetSecurityInfo(
        SafeFileHandle handle, int objectType, uint info, void* owner, void* group, void* dacl, void* sacl);

    // ---- privileges (the elevated helper only) ------------------------------------------------------

    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(
        SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, TOKEN_PRIVILEGES* state, uint length, nint previous, nint returned);

    // ---- user32: tell running programs the environment changed --------------------------------------

    public const nint HWND_BROADCAST = 0xFFFF;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessageTimeout(nint window, uint message, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);
}
