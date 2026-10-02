using Pathology.Core.Capture;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Tests.Capture;

namespace Pathology.Tests.Detection;

/// <summary>
/// A made-up machine for detector tests: PATH values, the folders that exist, who can write them. It goes through
/// the real <see cref="SnapshotCapturer"/> (with fakes underneath), so entries, keys and expansion are exactly what
/// a scan would produce. Paths are invented (<c>C:\Users\you\…</c>), never this machine's.
/// </summary>
internal sealed class TestMachine
{
    public const string You = "S-1-5-21-111-222-333-1001";
    public const string SomeoneElse = "S-1-5-21-111-222-333-1077";

    readonly FakeRegistry _registry;
    readonly FakeEnvironment _env = new();
    readonly FakeProbe _probe = new();
    readonly Dictionary<string, FolderSpec> _folders = new(StringComparer.OrdinalIgnoreCase);

    public TestMachine(string machinePath, string? userPath = null,
        PathValueKind machineKind = PathValueKind.ExpandString, PathValueKind userKind = PathValueKind.ExpandString)
    {
        _registry = new FakeRegistry(machinePath, userPath, machineKind, userKind);
        _registry.Machine["SystemRoot"] = @"C:\Windows";
        _registry.Machine["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        foreach (var (name, value) in new Dictionary<string, string>
                 {
                     ["SystemRoot"] = @"C:\Windows", ["windir"] = @"C:\Windows", ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
                     ["USERPROFILE"] = @"C:\Users\you", ["LOCALAPPDATA"] = @"C:\Users\you\AppData\Local",
                     ["ProgramFiles"] = @"C:\Program Files", ["ProgramData"] = @"C:\ProgramData",
                 })
            _env.NewProcess[name] = value;

        // The folders every Windows install has, locked down the way Windows ships them.
        Folder(@"C:\");
        Folder(@"C:\Windows");
        Folder(@"C:\Windows\System32", f => f.Files("where.exe", "cmd.exe", "net.exe", "notepad.exe"));
    }

    /// <summary>Administrator with UAC on (a split token). False makes this a standard user.</summary>
    public bool Admin { get; set; } = true;

    public bool? EnableLinkedConnections { get; set; }

    public bool? AdministratorProtection { get; set; }

    /// <summary>What a new process gets. Left null, it's the registry values' expansion (so CFG-01 stays quiet).</summary>
    public string? EffectivePath { get; set; }

    public string? ProcessPath { get; set; }

    public FakeRegistry Registry => _registry;
    public FakeEnvironment Environment => _env;

    /// <summary>Declare a folder that exists. Anything not declared doesn't.</summary>
    public TestMachine Folder(string path, Action<FolderSpec>? shape = null)
    {
        if (!_folders.TryGetValue(path, out var spec)) _folders[path] = spec = new FolderSpec(path);
        shape?.Invoke(spec);
        return this;
    }

    public PathSnapshot Snapshot()
    {
        foreach (var spec in _folders.Values)
        {
            _probe.With(spec.Path, f => spec.Apply(f));
            _probe.Files[spec.Path] = spec.FileNames.ToArray();
        }

        var host = new FakeHost();
        host.Info = host.Info with
        {
            Elevation = Admin ? ElevationType.Limited : ElevationType.Default,
            EnableLinkedConnections = EnableLinkedConnections,
            AdministratorProtection = AdministratorProtection,
        };
        var perspectives = Perspectives(Admin);
        var evaluator = new FakeEvaluator
        {
            Answer = (sddl, identity) => _folders.Values.First(f => f.Sddl == sddl).Access(identity),
        };

        var snapshot = new SnapshotCapturer(_registry, _env, host, new FixedPerspectives(perspectives), _probe, evaluator).Capture();
        var expected = string.Join(';', snapshot.Entries.Select(e => e.Expanded));
        return snapshot with { EffectivePath = EffectivePath ?? expected, ProcessPath = ProcessPath ?? EffectivePath ?? expected };
    }

    public Diagnosis Diagnose() => Diagnoser.Diagnose(Snapshot());

    public List<Finding> Findings(string rule) => Diagnose().Findings.Where(f => f.Rule == rule).ToList();

    public Finding Single(string rule) => Assert.Single(Findings(rule));

    static IReadOnlyList<PerspectiveIdentity> Perspectives(bool admin)
    {
        const GroupAttributes on = GroupAttributes.Enabled;
        TokenGroup[] everyday =
        [
            new(WellKnownSids.Everyone, on), new(WellKnownSids.Users, on), new(WellKnownSids.AuthenticatedUsers, on),
            new(WellKnownSids.Interactive, on),
        ];
        return
        [
            new() { Perspective = Perspective.CurrentUserUnelevated, UserSid = You, Groups = [.. everyday, new(WellKnownSids.Administrators, admin ? GroupAttributes.DenyOnly : GroupAttributes.None)] },
            new() { Perspective = Perspective.CurrentUserElevated, UserSid = You, Groups = [.. everyday, .. admin ? new[] { new TokenGroup(WellKnownSids.Administrators, on) } : []] },
            new() { Perspective = Perspective.System, UserSid = WellKnownSids.LocalSystem, Synthetic = true, Groups = [new(WellKnownSids.Administrators, on), new(WellKnownSids.Everyone, on)] },
            new() { Perspective = Perspective.StandardUser, UserSid = WellKnownSids.SyntheticStandardUser, Synthetic = true, Groups = everyday },
            new() { Perspective = Perspective.Sandboxed, UserSid = You, Synthetic = true, Groups = [.. everyday, new(WellKnownSids.LowIntegrity, GroupAttributes.Integrity)] },
        ];
    }

    sealed class FixedPerspectives(IReadOnlyList<PerspectiveIdentity> identities) : ITokenPerspectives
    {
        public IReadOnlyList<PerspectiveIdentity> Build() => identities;
    }
}

/// <summary>One folder of a <see cref="TestMachine"/>: its owner, who can write it, what's in it.</summary>
internal sealed class FolderSpec(string path)
{
    readonly Dictionary<Perspective, (FileAccessRights Rights, List<GrantingAce> Aces)> _grants = [];
    string _owner = WellKnownSids.Administrators;
    bool _ownerRightsAce;
    Func<DirectoryFacts, DirectoryFacts>? _shape;

    public string Path { get; } = path;
    public List<string> FileNames { get; } = [];

    /// <summary>Unique per folder, so the fake evaluator can tell which folder it's being asked about.</summary>
    public string Sddl => $"O:{_owner}G:SYD:(A;;FA;;;SY){(_ownerRightsAce ? "(A;;0x1200a9;;;OW)" : "")}S:(PATH:{Path})";

    /// <summary>Let <paramref name="who"/> add files, through an ACE for <paramref name="sid"/>.</summary>
    public FolderSpec WritableBy(Perspective who, string sid = WellKnownSids.Users, bool inherited = false,
        FileAccessRights rights = FileAccessRights.AddFile | FileAccessRights.AddSubdirectory)
    {
        var (granted, aces) = _grants.GetValueOrDefault(who, (FileAccessRights.None, []));
        aces.Add(new GrantingAce { Sid = sid, Mask = rights, Inherited = inherited });
        _grants[who] = (granted | rights, aces);
        return this;
    }

    /// <summary>Writable by every user (and so by you too), through an ACE for Users.</summary>
    public FolderSpec WritableByEveryone(bool inherited = false, string sid = WellKnownSids.Users) =>
        WritableBy(Perspective.StandardUser, sid, inherited).WritableBy(Perspective.CurrentUserUnelevated, sid, inherited);

    public FolderSpec WritableByYou() => WritableBy(Perspective.CurrentUserUnelevated, TestMachine.You, inherited: true, FileAccessRights.AddFile);

    /// <summary>Labelled Low integrity: writable by you and by sandboxed code running as you.</summary>
    public FolderSpec WritableBySandbox() =>
        WritableByYou().WritableBy(Perspective.Sandboxed, TestMachine.You, inherited: true, FileAccessRights.AddFile);

    /// <summary>Only folders can be created (the drive-root shape: Authenticated Users may create folders).</summary>
    public FolderSpec FoldersCreatableByEveryone() =>
        WritableBy(Perspective.StandardUser, WellKnownSids.AuthenticatedUsers, rights: FileAccessRights.AddSubdirectory)
            .WritableBy(Perspective.CurrentUserUnelevated, WellKnownSids.AuthenticatedUsers, rights: FileAccessRights.AddSubdirectory);

    public FolderSpec OwnedBy(string sid, bool limitedByOwnerRights = false)
    {
        _owner = sid;
        _ownerRightsAce = limitedByOwnerRights;
        return this;
    }

    public FolderSpec Files(params string[] names)
    {
        FileNames.AddRange(names);
        return this;
    }

    public FolderSpec Shape(Func<DirectoryFacts, DirectoryFacts> shape)
    {
        _shape = shape;
        return this;
    }

    public DirectoryFacts Apply(DirectoryFacts facts)
    {
        facts = facts with { OwnerSid = _owner, Sddl = Sddl };
        return _shape is null ? facts : _shape(facts);
    }

    public AccessResult Access(PerspectiveIdentity identity)
    {
        var (granted, aces) = _grants.GetValueOrDefault(identity.Perspective, (FileAccessRights.ListDirectory, []));
        var ownerIn = identity.AllowSids.Contains(_owner, StringComparer.OrdinalIgnoreCase);
        var dacFromOwner = ownerIn && !_ownerRightsAce && !granted.HasFlag(FileAccessRights.WriteDac);
        if (dacFromOwner) granted |= FileAccessRights.WriteDac;
        return new AccessResult
        {
            Perspective = identity.Perspective,
            Granted = granted | FileAccessRights.ListDirectory,
            OwnerInPerspective = ownerIn,
            WriteDacFromOwnership = dacFromOwner,
            GrantedBy = aces,
        };
    }
}
