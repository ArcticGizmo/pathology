using Pathology.Core.Capture;
using Pathology.Core.Model;

namespace Pathology.Tests.Capture;

/// <summary>
/// In-memory stand-ins for the Windows readers, so the capturer's orchestration is tested without touching
/// the machine. Paths are made up (<c>C:\Users\you\…</c>), never this machine's.
/// </summary>
internal sealed class FakeRegistry(string? machinePath, string? userPath = null,
    PathValueKind machineKind = PathValueKind.ExpandString, PathValueKind userKind = PathValueKind.ExpandString) : IRegistryPathReader
{
    public Dictionary<string, string> Machine { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> User { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Volatile { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RegistryEnvironment Read()
    {
        if (machinePath is not null) Machine["Path"] = machinePath;
        if (userPath is not null) User["Path"] = userPath;
        return new(
            machinePath is null ? RawPathValue.Missing(PathScope.Machine) : new() { Scope = PathScope.Machine, Value = machinePath, Kind = machineKind },
            userPath is null ? RawPathValue.Missing(PathScope.User) : new() { Scope = PathScope.User, Value = userPath, Kind = userKind },
            Machine, User, Volatile);
    }
}

internal sealed class FakeEnvironment : IEffectiveEnvironmentReader
{
    public Dictionary<string, string> NewProcess { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Current { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ReadNewProcessEnvironment() => NewProcess;
    public IReadOnlyDictionary<string, string> ReadCurrentProcessEnvironment() => Current;
}

internal sealed class FakeHost : IHostInfoReader
{
    public HostInfo Info { get; set; } = new()
    {
        MachineName = "DESKTOP-EXAMPLE",
        UserName = "you",
        UserDomain = "DESKTOP-EXAMPLE",
        UserSid = "S-1-5-21-111-222-333-1001",
        UserProfile = @"C:\Users\you",
    };

    public HostInfo Read() => Info;
}

internal sealed class FakePerspectives : ITokenPerspectives
{
    public IReadOnlyList<PerspectiveIdentity> Build() =>
    [
        new() { Perspective = Perspective.CurrentUserUnelevated, UserSid = "S-1-5-21-111-222-333-1001" },
        new() { Perspective = Perspective.CurrentUserElevated, UserSid = "S-1-5-21-111-222-333-1001" },
        new() { Perspective = Perspective.System, UserSid = WellKnownSids.LocalSystem, Synthetic = true },
        new() { Perspective = Perspective.StandardUser, UserSid = WellKnownSids.SyntheticStandardUser, Synthetic = true },
    ];
}

/// <summary>
/// A file system of known folders. Anything not listed doesn't exist. Records every path it was asked about,
/// so a test can prove the capturer never handed it a network path.
/// </summary>
internal sealed class FakeProbe : IDirectoryProbe
{
    public Dictionary<string, DirectoryFacts> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Path, bool AllowNetwork)> Calls { get; } = [];

    public FakeProbe With(string path, Func<DirectoryFacts, DirectoryFacts>? shape = null)
    {
        var facts = new DirectoryFacts
        {
            Path = path, Status = ProbeStatus.Probed, Exists = true, IsDirectory = true,
            Attributes = FileAttributes.Directory, Drive = DriveKind.Fixed,
            OwnerSid = WellKnownSids.Administrators, Sddl = "O:BAG:SYD:(A;;FA;;;SY)",
        };
        Folders[path] = shape is null ? facts : shape(facts);
        return this;
    }

    public DirectoryFacts Probe(string path, bool allowNetwork)
    {
        Calls.Add((path, allowNetwork));
        if (Folders.TryGetValue(path, out var facts)) return facts;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !allowNetwork)
            return new DirectoryFacts { Path = path, Status = ProbeStatus.SkippedNetwork, Drive = DriveKind.Unc };
        return new DirectoryFacts { Path = path, Status = ProbeStatus.Probed, Drive = DriveKind.Fixed };
    }
}

/// <summary>Grants a fixed mask to everyone, and records what it was asked.</summary>
internal sealed class FakeEvaluator : IAccessEvaluator
{
    public List<(string Sddl, Perspective Perspective)> Calls { get; } = [];
    public Func<string, PerspectiveIdentity, AccessResult>? Answer { get; set; }

    public AccessResult Evaluate(string sddl, PerspectiveIdentity identity)
    {
        Calls.Add((sddl, identity.Perspective));
        return Answer?.Invoke(sddl, identity) ?? new AccessResult { Perspective = identity.Perspective, Granted = FileAccessRights.ListDirectory };
    }
}
