using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pathology.Core.Model;
using static Pathology.Windows.Native.NativeMethods;

namespace Pathology.Windows.Native;

/// <summary>Queries on access tokens. Opens them for <c>TOKEN_QUERY</c> and reads; nothing is adjusted.</summary>
internal static unsafe class TokenReader
{
    public static SafeAccessTokenHandle OpenCurrent(uint access = TOKEN_QUERY)
    {
        if (!OpenProcessToken(GetCurrentProcess(), access, out var token)) throw new Win32Exception();
        return token;
    }

    public static string User(SafeAccessTokenHandle token) =>
        Query(token, TokenUser, buffer => SidToString(((SID_AND_ATTRIBUTES*)buffer)->Sid));

    public static List<TokenGroup> Groups(SafeAccessTokenHandle token) =>
        Query(token, TokenGroups, buffer =>
        {
            // TOKEN_GROUPS: a DWORD count, then an array of SID_AND_ATTRIBUTES at pointer alignment.
            var count = *(uint*)buffer;
            var groups = (SID_AND_ATTRIBUTES*)(buffer + IntPtr.Size);
            var result = new List<TokenGroup>((int)count);
            for (var i = 0; i < count; i++)
                result.Add(new TokenGroup(SidToString(groups[i].Sid), (GroupAttributes)groups[i].Attributes));
            return result;
        });

    public static ElevationType Elevation(SafeAccessTokenHandle token) =>
        Query(token, TokenElevationType, buffer => *(int*)buffer switch
        {
            1 => ElevationType.Default,
            2 => ElevationType.Full,
            3 => ElevationType.Limited,
            _ => ElevationType.Unknown,
        });

    /// <summary>
    /// The other half of a split UAC token. An unelevated caller gets an identification-level handle, which is
    /// enough to read its groups (and can't be used to act as it).
    /// </summary>
    public static SafeAccessTokenHandle Linked(SafeAccessTokenHandle token) =>
        Query(token, TokenLinkedToken, buffer => new SafeAccessTokenHandle(*(nint*)buffer));

    static T Query<T>(SafeAccessTokenHandle token, int infoClass, Func<nint, T> read)
    {
        GetTokenInformation(token, infoClass, null, 0, out var needed);
        if (needed == 0) throw new Win32Exception();
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, infoClass, (void*)buffer, needed, out _)) throw new Win32Exception();
            return read(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
