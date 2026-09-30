using Microsoft.Win32;
using Pathology.Core.Capture;
using Pathology.Core.Model;
using Pathology.Windows.Native;

namespace Pathology.Windows;

/// <summary>Machine, account, OS build and UAC state. Read-only, and local only (no account-name lookups).</summary>
public sealed class HostInfoReader : IHostInfoReader
{
    public HostInfo Read()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var version = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
        using var policy = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", writable: false);
        using var token = TokenReader.OpenCurrent();

        return new HostInfo
        {
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            // From the environment rather than Environment.UserDomainName, which can ask a domain controller.
            UserDomain = Environment.GetEnvironmentVariable("USERDOMAIN") ?? "",
            UserSid = TokenReader.User(token),
            UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OsVersion = OsVersion(version),
            OsDisplayVersion = version?.GetValue("DisplayVersion") as string,
            Elevation = TokenReader.Elevation(token),
            EnableLua = Flag(policy, "EnableLUA"),
            EnableLinkedConnections = Flag(policy, "EnableLinkedConnections"),
        };
    }

    static string OsVersion(RegistryKey? key)
    {
        var v = Environment.OSVersion.Version;
        var build = key?.GetValue("CurrentBuildNumber") as string ?? v.Build.ToString();
        return key?.GetValue("UBR") is int ubr ? $"{v.Major}.{v.Minor}.{build}.{ubr}" : $"{v.Major}.{v.Minor}.{build}";
    }

    static bool? Flag(RegistryKey? key, string name) => key?.GetValue(name) is int value ? value != 0 : null;
}
