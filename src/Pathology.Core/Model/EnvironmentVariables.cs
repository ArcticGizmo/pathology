namespace Pathology.Core.Model;

/// <summary>Where a set of environment variables was read from.</summary>
public enum EnvironmentSource
{
    /// <summary><c>HKLM\…\Session Manager\Environment</c>, unexpanded.</summary>
    Machine,

    /// <summary><c>HKCU\Environment</c>, unexpanded.</summary>
    User,

    /// <summary><c>HKCU\Volatile Environment</c>: the per-logon values (<c>USERPROFILE</c>, <c>APPDATA</c>, …).</summary>
    Volatile,

    /// <summary>The block a new process would get (<c>CreateEnvironmentBlock</c>), expanded.</summary>
    NewProcess,

    /// <summary>This process's own environment, inherited from whatever launched it.</summary>
    CurrentProcess,
}

/// <summary>
/// The variables defined in one <see cref="EnvironmentSource"/>. Every <b>name</b> is kept (the "defined only at
/// user scope" check needs them), but a <b>value</b> is kept only when a PATH value references it, directly or
/// through another variable, or it's on <see cref="Capture.EnvironmentCapturePolicy.AlwaysCaptured"/>.
/// Any other value is null: environment variables are where people keep tokens and keys.
/// </summary>
public sealed record EnvironmentVariables
{
    public EnvironmentSource Source { get; init; }

    public IReadOnlyDictionary<string, string?> Variables { get; init; } = new Dictionary<string, string?>();

    /// <summary>True when the variable is defined here (case-insensitive, as Windows compares them).</summary>
    public bool Defines(string name) => Find(name) is not null;

    /// <summary>The captured value, or null when undefined or when its value wasn't captured.</summary>
    public string? ValueOf(string name) => Find(name) is { } key ? Variables[key] : null;

    string? Find(string name)
    {
        if (Variables.ContainsKey(name)) return name;
        return Variables.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
    }
}
