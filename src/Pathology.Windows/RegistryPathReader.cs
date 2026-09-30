using Microsoft.Win32;
using Pathology.Core.Capture;
using Pathology.Core.Model;

namespace Pathology.Windows;

/// <summary>
/// Reads both stored PATH values <b>unexpanded</b>, with their value kind, plus every variable at the machine,
/// user and volatile scopes. Keys are opened read-only.
/// </summary>
public sealed class RegistryPathReader : IRegistryPathReader
{
    internal const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    internal const string UserEnvironmentKey = "Environment";
    internal const string VolatileEnvironmentKey = "Volatile Environment";

    public RegistryEnvironment Read()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var machine = hklm.OpenSubKey(MachineEnvironmentKey, writable: false);
        using var user = hkcu.OpenSubKey(UserEnvironmentKey, writable: false);
        using var @volatile = hkcu.OpenSubKey(VolatileEnvironmentKey, writable: false);

        return new RegistryEnvironment(
            ReadPath(machine, PathScope.Machine),
            ReadPath(user, PathScope.User),
            ReadAll(machine),
            ReadAll(user),
            ReadAll(@volatile));
    }

    static RawPathValue ReadPath(RegistryKey? key, PathScope scope)
    {
        var name = key?.GetValueNames().FirstOrDefault(n => string.Equals(n, "Path", StringComparison.OrdinalIgnoreCase));
        if (key is null || name is null) return RawPathValue.Missing(scope);

        var kind = key.GetValueKind(name) switch
        {
            RegistryValueKind.String => PathValueKind.String,
            RegistryValueKind.ExpandString => PathValueKind.ExpandString,
            _ => PathValueKind.Other,
        };
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new RawPathValue { Scope = scope, Kind = kind, Value = value as string ?? value?.ToString() };
    }

    static Dictionary<string, string> ReadAll(RegistryKey? key)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (key is null) return values;
        foreach (var name in key.GetValueNames())
        {
            // The unnamed default value isn't an environment variable.
            if (name.Length == 0) continue;
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[name] = value as string ?? value?.ToString() ?? "";
        }
        return values;
    }
}
