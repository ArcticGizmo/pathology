using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pathology.Windows.Native;

/// <summary>
/// The Win32 surface PATHology reads through. Every call here is a query: no file, registry value or ACL is
/// created or changed. (The only write-shaped call, <c>DeviceIoControl</c>, is used with
/// <c>FSCTL_GET_REPARSE_POINT</c>, which reads.)
/// </summary>
internal static unsafe partial class NativeMethods
{
    // ---- kernel32: files ----------------------------------------------------------------------------

    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint READ_CONTROL = 0x20000;
    public const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    public const int FileAttributeTagInfo = 9;
    public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_DRIVE = 15;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_BAD_NETPATH = 53;
    public const int ERROR_BAD_NET_NAME = 67;
    public const int ERROR_INVALID_NAME = 123;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NOT_A_REPARSE_POINT = 4390;

    [StructLayout(LayoutKind.Sequential)]
    public struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileAttributesEx(string name, int infoLevel, out WIN32_FILE_ATTRIBUTE_DATA data);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle device, uint code, void* inBuffer, uint inSize, void* outBuffer, uint outSize,
        out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveType(string root);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint QueryDosDevice(string device, char* target, uint max);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetLongPathName(string shortPath, char* longPath, uint size);

    public const uint SEM_FAILCRITICALERRORS = 0x0001;
    public const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadErrorMode(uint newMode, out uint oldMode);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    // ---- ntdll: an open that asks for exactly the rights named ------------------------------------
    // CreateFileW quietly adds SYNCHRONIZE | FILE_READ_ATTRIBUTES to every request. A folder's owner holds
    // READ_CONTROL implicitly but nothing else, so on an owner-only folder CreateFile fails where
    // NtOpenFile(READ_CONTROL) succeeds.

    public const uint OBJ_CASE_INSENSITIVE = 0x40;
    public const uint FILE_OPEN_FOR_BACKUP_INTENT = 0x00004000;
    public const uint FILE_OPEN_REPARSE_POINT = 0x00200000;

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OBJECT_ATTRIBUTES
    {
        public int Length;
        public nint RootDirectory;
        public UNICODE_STRING* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_STATUS_BLOCK
    {
        public nint Status;
        public nint Information;
    }

    [LibraryImport("ntdll.dll")]
    public static partial int NtOpenFile(
        out SafeFileHandle handle, uint access, OBJECT_ATTRIBUTES* attributes, IO_STATUS_BLOCK* status, uint share, uint options);

    [LibraryImport("ntdll.dll")]
    public static partial uint RtlNtStatusToDosError(int status);

    // ---- mpr: mapped drives (reads the local connection table) --------------------------------------

    [LibraryImport("mpr.dll", EntryPoint = "WNetGetConnectionW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WNetGetConnection(string localName, char* remoteName, ref uint length);

    // ---- advapi32: security descriptors, SIDs, tokens -----------------------------------------------

    public const int SE_FILE_OBJECT = 1;
    public const uint OWNER_SECURITY_INFORMATION = 0x1;
    public const uint GROUP_SECURITY_INFORMATION = 0x2;
    public const uint DACL_SECURITY_INFORMATION = 0x4;
    public const uint LABEL_SECURITY_INFORMATION = 0x10;
    public const uint SDDL_REVISION_1 = 1;

    [LibraryImport("advapi32.dll")]
    public static partial uint GetSecurityInfo(
        SafeFileHandle handle, int objectType, uint info, nint* owner, nint* group, nint* dacl, nint* sacl, nint* descriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertSecurityDescriptorToStringSecurityDescriptor(
        nint descriptor, uint revision, uint info, out nint sddl, out uint length);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string sddl, uint revision, out nint descriptor, out uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertSidToStringSid(nint sid, out nint text);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertStringSidToSid(string text, out nint sid);

    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_IMPERSONATE = 0x0004;
    public const uint TOKEN_QUERY = 0x0008;

    public const int TokenUser = 1;
    public const int TokenGroups = 2;
    public const int TokenElevationType = 18;
    public const int TokenLinkedToken = 19;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, void* info, uint length, out uint returned);

    [StructLayout(LayoutKind.Sequential)]
    public struct SID_AND_ATTRIBUTES
    {
        public nint Sid;
        public uint Attributes;
    }

    // ---- userenv: the environment a new process gets -----------------------------------------------

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyEnvironmentBlock(nint environment);

    // ---- authz: access checks over a captured descriptor --------------------------------------------

    public const uint AUTHZ_RM_FLAG_NO_AUDIT = 0x1;
    public const uint AUTHZ_SKIP_TOKEN_GROUPS = 0x2;
    public const uint MAXIMUM_ALLOWED = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AUTHZ_ACCESS_REQUEST
    {
        public uint DesiredAccess;
        public nint PrincipalSelfSid;
        public nint ObjectTypeList;
        public uint ObjectTypeListLength;
        public nint OptionalArguments;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AUTHZ_ACCESS_REPLY
    {
        public uint ResultListLength;
        public uint* GrantedAccessMask;
        public uint* SaclEvaluationResults;
        public uint* Error;
    }

    [LibraryImport("authz.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AuthzInitializeResourceManager(
        uint flags, nint accessCheck, nint computeDynamicGroups, nint freeDynamicGroups, string? name, out nint resourceManager);

    [LibraryImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AuthzFreeResourceManager(nint resourceManager);

    [LibraryImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AuthzInitializeContextFromSid(
        uint flags, nint userSid, nint resourceManager, nint expiration, LUID identifier, nint dynamicGroupArgs, out nint context);

    [LibraryImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AuthzAddSidsToContext(
        nint context, SID_AND_ATTRIBUTES* sids, uint sidCount, SID_AND_ATTRIBUTES* restrictedSids, uint restrictedCount, out nint newContext);

    [LibraryImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AuthzAccessCheck(
        uint flags, nint context, AUTHZ_ACCESS_REQUEST* request, nint auditEvent, nint descriptor,
        nint* optionalDescriptors, uint optionalCount, AUTHZ_ACCESS_REPLY* reply, nint* results);

    [LibraryImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AuthzFreeContext(nint context);

    // ---- helpers ------------------------------------------------------------------------------------

    /// <summary>A SID pointer as its string form.</summary>
    public static string SidToString(nint sid)
    {
        if (!ConvertSidToStringSid(sid, out var text)) throw new System.ComponentModel.Win32Exception();
        try { return Marshal.PtrToStringUni(text)!; }
        finally { LocalFree(text); }
    }
}
