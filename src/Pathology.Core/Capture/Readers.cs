using Pathology.Core.Model;

namespace Pathology.Core.Capture;

/// <summary>What the registry holds: both PATH values raw, and every variable at each registry scope.</summary>
/// <param name="MachineVariables">HKLM variables, unexpanded, keyed case-insensitively.</param>
/// <param name="UserVariables">HKCU\Environment variables, unexpanded.</param>
/// <param name="VolatileVariables">HKCU\Volatile Environment variables (the per-logon profile values).</param>
public sealed record RegistryEnvironment(
    RawPathValue MachinePath,
    RawPathValue UserPath,
    IReadOnlyDictionary<string, string> MachineVariables,
    IReadOnlyDictionary<string, string> UserVariables,
    IReadOnlyDictionary<string, string> VolatileVariables);

/// <summary>Reads the stored PATH values and environment variables. Read-only; never expands anything.</summary>
public interface IRegistryPathReader
{
    RegistryEnvironment Read();
}

/// <summary>The environment a new process would get, and the one this process actually has.</summary>
public interface IEffectiveEnvironmentReader
{
    /// <summary>What <c>CreateEnvironmentBlock(token, bInherit: false)</c> builds from the registry right now.</summary>
    IReadOnlyDictionary<string, string> ReadNewProcessEnvironment();

    /// <summary>This process's own environment (inherited from Explorer or a shell, so possibly stale).</summary>
    IReadOnlyDictionary<string, string> ReadCurrentProcessEnvironment();
}

/// <summary>Who and where: machine, user, OS build, UAC state.</summary>
public interface IHostInfoReader
{
    HostInfo Read();
}

/// <summary>Builds the SID set for every <see cref="Perspective"/>.</summary>
public interface ITokenPerspectives
{
    /// <summary>One identity per perspective, always all four.</summary>
    IReadOnlyList<PerspectiveIdentity> Build();
}

/// <summary>
/// Reads one directory's facts without writing anything. Implementations must refuse to touch the network
/// (UNC paths, mapped drives, links that lead to either) unless <paramref name="allowNetwork"/> is set, and must
/// not let a removable drive raise an "insert a disk" prompt.
/// </summary>
public interface IDirectoryProbe
{
    /// <param name="path">A canonical, fully qualified path (see <see cref="Normalisation.PathText.Canonical"/>).</param>
    DirectoryFacts Probe(string path, bool allowNetwork);
}

/// <summary>Evaluates a captured security descriptor for one perspective. Never writes or probes.</summary>
public interface IAccessEvaluator
{
    AccessResult Evaluate(string sddl, PerspectiveIdentity identity);
}
