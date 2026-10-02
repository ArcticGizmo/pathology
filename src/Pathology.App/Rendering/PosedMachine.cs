using Pathology.Core.Capture;
using Pathology.Core.Model;

namespace Pathology.App.Rendering;

/// <summary>
/// A made-up PC for the headless renderer: PATH values, the folders that exist, who can write them and what's in
/// them. It goes through the real <see cref="SnapshotCapturer"/> over in-memory readers, so a posed page shows
/// exactly what a scan of such a machine would. Every path is invented (<c>C:\Users\you\…</c>), never this
/// machine's, so renders are safe to share.
/// </summary>
internal sealed class PosedMachine
{
    public const string You = "S-1-5-21-111-222-333-1001";

    readonly string? _machinePath;
    readonly string? _userPath;
    readonly Dictionary<string, PosedFolder> _folders = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> MachineVariables { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.WSF;.MSC",
    };

    public Dictionary<string, string> UserVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> NewProcess { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows", ["windir"] = @"C:\Windows", ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.WSF;.MSC",
        ["USERPROFILE"] = @"C:\Users\you", ["LOCALAPPDATA"] = @"C:\Users\you\AppData\Local",
        ["APPDATA"] = @"C:\Users\you\AppData\Roaming", ["ProgramFiles"] = @"C:\Program Files",
        ["ProgramData"] = @"C:\ProgramData",
    };

    /// <param name="machinePath">The machine PATH as stored, or null for none at all.</param>
    /// <param name="userPath">The user PATH as stored, or null for none at all.</param>
    public PosedMachine(string? machinePath, string? userPath)
    {
        _machinePath = machinePath;
        _userPath = userPath;
        Folder(@"C:\");
        Folder(@"C:\Windows", f => f.Files("explorer.exe", "notepad.exe", "regedit.exe"));
        Folder(@"C:\Windows\System32", f => f.Files(
            "cmd.exe", "where.exe", "net.exe", "sc.exe", "reg.exe", "whoami.exe", "ping.exe", "tasklist.exe",
            "taskkill.exe", "schtasks.exe", "msiexec.exe", "rundll32.exe", "curl.exe", "tar.exe", "notepad.exe",
            "conhost.exe", "find.exe", "sort.exe", "more.com", "tree.com", "ipconfig.exe", "hostname.exe"));
    }

    /// <summary>Administrator with UAC on (a split token). False makes this a standard user.</summary>
    public bool Admin { get; init; } = true;

    public DateTimeOffset CapturedAt { get; init; } = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    /// <summary>How the machine PATH is stored. <c>REG_SZ</c> leaves its <c>%SystemRoot%</c> entries literal.</summary>
    public PathValueKind MachineKind { get; init; } = PathValueKind.ExpandString;

    /// <summary>Declare a folder that exists. Anything not declared doesn't.</summary>
    public PosedMachine Folder(string path, Action<PosedFolder>? shape = null)
    {
        if (!_folders.TryGetValue(path, out var spec)) _folders[path] = spec = new PosedFolder(path);
        shape?.Invoke(spec);
        return this;
    }

    public PathSnapshot Snapshot()
    {
        var registry = new Registry(this);
        var snapshot = new SnapshotCapturer(
                registry, new Environment(NewProcess), new Host(Admin), new Perspectives(Admin), new FolderProbe(_folders),
                new Evaluator(_folders), new FixedClock(CapturedAt))
            .Capture();
        var effective = string.Join(';', snapshot.Entries.Select(e => e.Expanded));
        return snapshot with { EffectivePath = effective, ProcessPath = effective };
    }

    sealed class Registry(PosedMachine m) : IRegistryPathReader
    {
        public RegistryEnvironment Read()
        {
            var machine = new Dictionary<string, string>(m.MachineVariables, StringComparer.OrdinalIgnoreCase);
            if (m._machinePath is not null) machine["Path"] = m._machinePath;
            var user = new Dictionary<string, string>(m.UserVariables, StringComparer.OrdinalIgnoreCase);
            if (m._userPath is not null) user["Path"] = m._userPath;
            return new(
                m._machinePath is null
                    ? RawPathValue.Missing(PathScope.Machine)
                    : new RawPathValue { Scope = PathScope.Machine, Value = m._machinePath, Kind = m.MachineKind },
                m._userPath is null
                    ? RawPathValue.Missing(PathScope.User)
                    : new RawPathValue { Scope = PathScope.User, Value = m._userPath, Kind = PathValueKind.ExpandString },
                machine, user, new Dictionary<string, string>());
        }
    }

    sealed class Environment(Dictionary<string, string> newProcess) : IEffectiveEnvironmentReader
    {
        public IReadOnlyDictionary<string, string> ReadNewProcessEnvironment() => newProcess;
        public IReadOnlyDictionary<string, string> ReadCurrentProcessEnvironment() => newProcess;
    }

    sealed class Host(bool admin) : IHostInfoReader
    {
        public HostInfo Read() => new()
        {
            MachineName = "DESKTOP-EXAMPLE", UserName = "you", UserDomain = "DESKTOP-EXAMPLE", UserSid = You,
            UserProfile = @"C:\Users\you", OsVersion = "10.0.26200.6584", OsDisplayVersion = "25H2",
            Elevation = admin ? ElevationType.Limited : ElevationType.Default, EnableLua = true,
        };
    }

    sealed class Perspectives(bool admin) : ITokenPerspectives
    {
        public IReadOnlyList<PerspectiveIdentity> Build()
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
                new() { Perspective = Perspective.Sandboxed, UserSid = You, Synthetic = true, Groups = everyday },
            ];
        }
    }

    sealed class FolderProbe(Dictionary<string, PosedFolder> folders) : IDirectoryProbe
    {
        public DirectoryFacts Probe(string path, bool allowNetwork)
        {
            if (folders.TryGetValue(path, out var spec)) return spec.Facts();
            if (path.StartsWith(@"\\", StringComparison.Ordinal) && !allowNetwork)
                return new DirectoryFacts { Path = path, Status = ProbeStatus.SkippedNetwork, Drive = DriveKind.Unc };
            return new DirectoryFacts { Path = path, Status = ProbeStatus.Probed, Drive = DriveKind.Fixed };
        }

        public IReadOnlyList<string>? ListFiles(string path, IReadOnlySet<string> extensions, bool allowNetwork) =>
            folders.TryGetValue(path, out var spec)
                ? spec.FileNames.Where(n => extensions.Contains(System.IO.Path.GetExtension(n))).ToList()
                : [];
    }

    sealed class Evaluator(Dictionary<string, PosedFolder> folders) : IAccessEvaluator
    {
        public AccessResult Evaluate(string sddl, PerspectiveIdentity identity) =>
            folders.Values.First(f => f.Sddl == sddl).Access(identity);
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>One folder of a <see cref="PosedMachine"/>: its owner, who can write it, what's in it.</summary>
internal sealed class PosedFolder(string path)
{
    readonly Dictionary<Perspective, (FileAccessRights Rights, List<GrantingAce> Aces)> _grants = [];
    string _owner = WellKnownSids.Administrators;
    Func<DirectoryFacts, DirectoryFacts>? _shape;

    public string Path { get; } = path;
    public List<string> FileNames { get; } = [];

    /// <summary>Unique per folder, so the evaluator can tell which folder it's being asked about.</summary>
    public string Sddl => $"O:{_owner}G:SYD:(A;;FA;;;SY)S:(PATH:{Path})";

    public PosedFolder WritableBy(Perspective who, string sid, bool inherited = false,
        FileAccessRights rights = FileAccessRights.AddFile | FileAccessRights.AddSubdirectory)
    {
        var (granted, aces) = _grants.GetValueOrDefault(who, (FileAccessRights.None, []));
        aces.Add(new GrantingAce { Sid = sid, Mask = rights, Inherited = inherited });
        _grants[who] = (granted | rights, aces);
        return this;
    }

    /// <summary>Every user can add files (and so can you), through an ACE for <paramref name="sid"/>.</summary>
    public PosedFolder WritableByEveryone(bool inherited = false, string sid = WellKnownSids.Users) =>
        WritableBy(Perspective.StandardUser, sid, inherited).WritableBy(Perspective.CurrentUserUnelevated, sid, inherited);

    /// <summary>Only you can add files: a folder under your profile.</summary>
    public PosedFolder WritableByYou() =>
        WritableBy(Perspective.CurrentUserUnelevated, PosedMachine.You, inherited: true).WritableBy(Perspective.CurrentUserElevated, PosedMachine.You, inherited: true);

    /// <summary>Labelled Low integrity: you, and sandboxed code running as you, can add files.</summary>
    public PosedFolder WritableBySandbox() =>
        WritableByYou().WritableBy(Perspective.Sandboxed, PosedMachine.You, inherited: true, FileAccessRights.AddFile);

    /// <summary>The drive-root shape: Authenticated Users may create folders.</summary>
    public PosedFolder FoldersCreatableByEveryone() =>
        WritableBy(Perspective.StandardUser, WellKnownSids.AuthenticatedUsers, rights: FileAccessRights.AddSubdirectory)
            .WritableBy(Perspective.CurrentUserUnelevated, WellKnownSids.AuthenticatedUsers, rights: FileAccessRights.AddSubdirectory);

    public PosedFolder OwnedBy(string sid)
    {
        _owner = sid;
        return this;
    }

    public PosedFolder Files(params string[] names)
    {
        FileNames.AddRange(names);
        return this;
    }

    public PosedFolder Shape(Func<DirectoryFacts, DirectoryFacts> shape)
    {
        _shape = shape;
        return this;
    }

    public DirectoryFacts Facts()
    {
        var facts = new DirectoryFacts
        {
            Path = Path, Status = ProbeStatus.Probed, Exists = true, IsDirectory = true,
            Attributes = FileAttributes.Directory, Drive = DriveKind.Fixed, OwnerSid = _owner, Sddl = Sddl,
        };
        return _shape is null ? facts : _shape(facts);
    }

    public AccessResult Access(PerspectiveIdentity identity)
    {
        var (granted, aces) = _grants.GetValueOrDefault(identity.Perspective, (FileAccessRights.ListDirectory, []));
        var ownerIn = identity.AllowSids.Contains(_owner, StringComparer.OrdinalIgnoreCase);
        var dacFromOwner = ownerIn && !granted.HasFlag(FileAccessRights.WriteDac);
        if (dacFromOwner) granted |= FileAccessRights.WriteDac;
        return new AccessResult
        {
            Perspective = identity.Perspective,
            Granted = granted | FileAccessRights.ListDirectory | FileAccessRights.ReadControl,
            OwnerInPerspective = ownerIn,
            WriteDacFromOwnership = dacFromOwner,
            GrantedBy = aces,
        };
    }
}

/// <summary>The machines the renderer poses.</summary>
internal static class PosedMachines
{
    /// <summary>
    /// A developer's PC that has collected the usual problems: a writable tools folder ahead of System32, a
    /// Python installed at the drive root, a variable only the user defines, a dead folder that anyone could
    /// create, duplicates, stray quotes and spaces, two Pythons fighting, and a LocalLow folder sandboxes can write.
    /// </summary>
    public static PathSnapshot Messy()
    {
        var m = new PosedMachine(
            @"C:\Tools;%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\System32\Wbem;C:\Program Files\Git\cmd;" +
            @"C:\Python312\;C:\Python312\Scripts\;C:\Program Files\nodejs\;%JAVA_HOME%\bin;" +
            @"C:\Users\you\AppData\Local\Programs\Microsoft VS Code\bin;C:\OldApp\bin;C:\Program Files\Git\cmd",
            @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps;C:\Users\you\.dotnet\tools;""C:\Users\you\bin"";" +
            @"C:\Users\you\scoop\shims ;C:\Users\you\AppData\LocalLow\Acme\bin;;");
        m.UserVariables["JAVA_HOME"] = @"C:\Program Files\Eclipse Adoptium\jdk-21";

        m.Folder(@"C:\", f => f.FoldersCreatableByEveryone());
        m.Folder(@"C:\Tools", f => f.WritableByEveryone().Files("where.bat", "7z.exe", "jq.exe", "rg.exe"));
        m.Folder(@"C:\Windows\System32\Wbem", f => f.Files("wmic.exe"));
        m.Folder(@"C:\Program Files\Git\cmd", f => f.Files("git.exe", "gitk.exe", "scalar.exe"));
        m.Folder(@"C:\Python312", f => f.WritableByEveryone(inherited: true, WellKnownSids.AuthenticatedUsers).Files("python.exe", "pythonw.exe"));
        m.Folder(@"C:\Python312\Scripts", f => f.WritableByEveryone(inherited: true, WellKnownSids.AuthenticatedUsers).Files("pip.exe", "pip3.exe"));
        m.Folder(@"C:\Program Files\nodejs", f => f.Files("node.exe", "npm.cmd", "npx.cmd"));
        m.Folder(@"C:\Program Files\Eclipse Adoptium\jdk-21\bin", f => f.Files("java.exe", "javac.exe", "jar.exe"));
        m.Folder(@"C:\Users\you", f => f.WritableByYou());
        m.Folder(@"C:\Users\you\AppData\Local\Programs\Microsoft VS Code\bin", f => f.WritableByYou().Files("code.cmd"));
        m.Folder(@"C:\Users\you\AppData\Local\Microsoft\WindowsApps", f => f.WritableByYou().Files("python.exe", "python3.exe", "winget.exe", "wt.exe"));
        m.Folder(@"C:\Users\you\.dotnet\tools", f => f.WritableByYou().Files("dotnet-ef.exe"));
        m.Folder(@"C:\Users\you\bin", f => f.WritableByYou().Files("jq.exe"));
        m.Folder(@"C:\Users\you\scoop\shims", f => f.WritableByYou().Files("rg.exe", "fd.exe", "7z.exe"));
        m.Folder(@"C:\Users\you\AppData\LocalLow\Acme\bin", f => f.WritableBySandbox().Files("acme.exe"));
        return m.Snapshot();
    }

    /// <summary>
    /// The worst case: a standard user's PC whose machine PATH was saved as REG_SZ (so every Windows folder in it
    /// is dead), with a relative entry, a tools folder anyone can write, and a user PATH setx cut at 1,024.
    /// </summary>
    public static PathSnapshot Worst()
    {
        var setx = @"C:\Users\you\bin;" + string.Join(';', Enumerable.Range(1, 60).Select(i => $@"C:\Users\you\tools\t{i:00}"));
        var m = new PosedMachine(@"C:\Tools;.;%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\System32\Wbem", setx[..1024])
        {
            Admin = false,
            MachineKind = PathValueKind.String,
        };
        m.Folder(@"C:\", f => f.FoldersCreatableByEveryone());
        m.Folder(@"C:\Tools", f => f.WritableByEveryone().Files("where.bat", "net.cmd", "jq.exe"));
        m.Folder(@"C:\Users\you", f => f.WritableByYou());
        m.Folder(@"C:\Users\you\bin", f => f.WritableByYou().Files("jq.exe"));
        return m.Snapshot();
    }

    /// <summary>No PATH at all, at either scope: what the pages say when there's nothing to judge.</summary>
    public static PathSnapshot Empty() => new PosedMachine(null, null).Snapshot();

    /// <summary>A fresh Windows install: the stock machine PATH and the WindowsApps folder every user gets.</summary>
    public static PathSnapshot Clean()
    {
        var m = new PosedMachine(
            @"%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\System32\Wbem;%SYSTEMROOT%\System32\WindowsPowerShell\v1.0\;%SYSTEMROOT%\System32\OpenSSH\",
            @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps;");
        m.Folder(@"C:\Windows\System32\Wbem", f => f.Files("wmic.exe"));
        m.Folder(@"C:\Windows\System32\WindowsPowerShell\v1.0", f => f.Files("powershell.exe"));
        m.Folder(@"C:\Windows\System32\OpenSSH", f => f.Files("ssh.exe", "scp.exe", "sftp.exe"));
        m.Folder(@"C:\Users\you", f => f.WritableByYou());
        m.Folder(@"C:\Users\you\AppData\Local\Microsoft\WindowsApps", f => f.WritableByYou().Files("winget.exe", "wt.exe"));
        return m.Snapshot();
    }
}
