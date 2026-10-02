namespace Pathology.Core.Model;

/// <summary>Which stored PATH value an entry came from.</summary>
public enum PathScope
{
    /// <summary><c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment</c>: every user, and SYSTEM.</summary>
    Machine,

    /// <summary><c>HKCU\Environment</c>: this user only, appended after the machine PATH.</summary>
    User,
}

/// <summary>
/// The registry value kind a PATH is stored as. Named after <c>Microsoft.Win32.RegistryValueKind</c>, which
/// Core can't reference (it stays OS-agnostic).
/// </summary>
public enum PathValueKind
{
    /// <summary>No <c>Path</c> value at that scope.</summary>
    Missing,

    /// <summary><c>REG_SZ</c>: stored literally; Windows does <b>not</b> expand <c>%VAR%</c> inside it.</summary>
    String,

    /// <summary><c>REG_EXPAND_SZ</c>: <c>%VAR%</c> references are expanded when the environment is built.</summary>
    ExpandString,

    /// <summary>Some other kind (binary, multi-string, …). Windows ignores it as a PATH.</summary>
    Other,
}
