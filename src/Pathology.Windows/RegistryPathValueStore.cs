using Microsoft.Win32;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Windows;

/// <summary>
/// Reads and writes the stored PATH values. <b>A writer</b>: built only by <c>AppServices</c> (the user PATH) and
/// the elevated helper's verb (the machine PATH), and called only from an apply the user clicked. It has no test
/// against the real registry, by design (see CLAUDE.md); its logic is the few lines below.
/// </summary>
/// <remarks>
/// The value is written as the very string planned, under its existing name, with the planned kind: a
/// <c>REG_EXPAND_SZ</c> stays one, nothing is expanded on the way, and it's never truncated (a value Windows
/// couldn't hold is refused). <c>setx</c> is never involved. The caller reads it back to verify.
/// </remarks>
public sealed class RegistryPathValueStore : IPathValueStore
{
    public RawPathValue Read(PathScope scope)
    {
        using var hive = Hive(scope);
        using var key = hive.OpenSubKey(KeyPath(scope), writable: false);
        return RegistryPathReader.ReadPath(key, scope);
    }

    public void Write(RawPathValue value)
    {
        if (value.Kind is not (PathValueKind.String or PathValueKind.ExpandString or PathValueKind.Missing))
            throw new InvalidOperationException("Only REG_SZ and REG_EXPAND_SZ PATH values are written.");
        if (value.Length > ChangeSet.MaxValueLength)
            throw new InvalidOperationException($"The value is longer than Windows can hold ({ChangeSet.MaxValueLength} characters), so it wasn't written.");

        using var hive = Hive(value.Scope);
        using var key = value.Scope == PathScope.User
            ? hive.CreateSubKey(KeyPath(value.Scope), writable: true)
            : hive.OpenSubKey(KeyPath(value.Scope), writable: true)
              ?? throw new InvalidOperationException("The machine environment key is missing.");

        var name = key.GetValueNames().FirstOrDefault(n => string.Equals(n, "Path", StringComparison.OrdinalIgnoreCase)) ?? "Path";
        if (value.Kind == PathValueKind.Missing)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }
        key.SetValue(name, value.Value ?? "", value.Kind == PathValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    static RegistryKey Hive(PathScope scope) => RegistryKey.OpenBaseKey(
        scope == PathScope.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    static string KeyPath(PathScope scope) =>
        scope == PathScope.Machine ? RegistryPathReader.MachineEnvironmentKey : RegistryPathReader.UserEnvironmentKey;
}

/// <summary>
/// Tells running programs that the environment changed (<c>WM_SETTINGCHANGE</c> "Environment"), so Explorer, and
/// what it starts next, pick up the new PATH without a sign-out.
/// </summary>
public sealed class EnvironmentBroadcast : IEnvironmentBroadcast
{
    public bool Broadcast() =>
        Native.NativeMethods.SendMessageTimeout(Native.NativeMethods.HWND_BROADCAST, Native.NativeMethods.WM_SETTINGCHANGE, 0,
            "Environment", Native.NativeMethods.SMTO_ABORTIFHUNG, 5000, out _) != 0;
}
