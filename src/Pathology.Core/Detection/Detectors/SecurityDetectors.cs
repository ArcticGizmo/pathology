using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Core.Detection.Detectors;

/// <summary>
/// SEC-05: folders whose write-for-everyone access is all inherited from the drive root (or ProgramData).
/// One finding per source covering every affected folder; the per-folder findings share its root cause.
/// </summary>
public sealed class InheritedPermissiveAcl : IDetector
{
    public string Rule => "SEC-05";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        var bySource = new Dictionary<string, (string Display, List<(PathEntry Entry, DirectoryFacts Folder)> Hits)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in context.Entries)
        {
            if (context.Resolve(entry).Final is not { Exists: true, IsDirectory: true } folder) continue;
            if (Threats.InheritedFrom(context, folder) is not { } source || !seen.Add(PathText.Key(folder.Path))) continue;
            if (!bySource.TryGetValue(source.Key, out var group)) bySource[source.Key] = group = (source.Display, []);
            group.Hits.Add((entry, folder));
        }

        foreach (var (key, (display, hits)) in bySource)
        {
            var machine = hits.Any(h => h.Entry.Scope == PathScope.Machine);
            var scopes = hits.Select(h => h.Entry.Scope).Distinct().ToList();
            var isRoot = PathText.RootOf(display) is { } root && PathText.Key(root) == key;
            var folders = Words.List(hits.Select(h => h.Folder.Path));

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Security,
                Severity = machine ? Severity.High : Severity.Medium,
                Subject = display,
                RootCause = "inherited:" + key,
                Title = $"{Words.Count(hits.Count, "PATH folder")} inherit write access for every user from {display}",
                What = $"{folders} get {(hits.Count == 1 ? "its" : "their")} permissions from {display}, which lets every " +
                       "signed-in user add files to the folders below it. Nothing on the way down takes that right back.",
                Why = (isRoot
                          ? "A folder created directly under a drive root inherits Authenticated Users' modify right. "
                          : $"{display} lets every user write into the subfolders it passes its permissions to. ") +
                      (machine
                          ? "Services running as SYSTEM search the machine PATH, so a file planted in any of these folders runs as SYSTEM. "
                          : "Every program you run searches your PATH, so another account could plant a file that runs as you. ") +
                      "Fixing the folders one at a time misses the next one an installer creates.",
                Fix = "Give each folder its own permissions: turn off inheritance, then remove the write entries for Users, " +
                      "Authenticated Users and Everyone, leaving Administrators and SYSTEM with full control and Users with read & " +
                      "execute. Tools installed under Program Files get locked-down permissions to begin with.",
                Scope = scopes.Count == 1 ? scopes[0] : null,
                Entries = hits.Select(h => EntryRef.Of(h.Entry)).ToList(),
                Perspectives = machine
                    ? [Perspective.StandardUser, Perspective.System, Perspective.CurrentUserElevated]
                    : [Perspective.StandardUser, Perspective.CurrentUserUnelevated],
                Evidence = hits.SelectMany(h => Threats.AccessEvidence(h.Folder, Attacker.AnyUser, context).Select(line => $"{h.Folder.Path}: {line}")).ToList(),
                Learn = LearnTopics.DllSearchOrder,
            };
        }
    }
}

/// <summary>SEC-01: a machine PATH folder that a non-admin can add files to.</summary>
public sealed class MachineFolderWritable : IDetector
{
    public string Rule => "SEC-01";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var entry in context.Snapshot.EntriesIn(PathScope.Machine))
        {
            var resolved = context.Resolve(entry);
            if (resolved.ViaLink || resolved.Final is not { Exists: true, IsDirectory: true } folder) continue;

            // A WRITE_DAC that comes only from ownership is SEC-02's to explain.
            var attacker = Threats.WhoCanPlant(folder, countOwnership: false);
            if (attacker == Attacker.None) continue;

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Security,
                Severity = Severity.High,
                Subject = folder.Path,
                RootCause = Threats.WritableRootCause(context, folder),
                Title = attacker == Attacker.AnyUser
                    ? $"Any user can add files to {folder.Path}, which is in the machine PATH"
                    : $"You can add files to {folder.Path} without elevating, and it's in the machine PATH",
                What = $"{Words.Entry(entry)} is in the machine PATH, and {Threats.Who(attacker)} can create files in it.",
                Why = "Services running as SYSTEM, and every elevated program, search the machine PATH: for commands, and for " +
                      "DLLs they don't find anywhere else first. A file planted here runs with their rights. " +
                      (Threats.IsEscalation(attacker, context)
                          ? "That's a local privilege escalation to SYSTEM."
                          : "Anything running as you could use it to reach SYSTEM without a UAC prompt."),
                Fix = "Lock the folder down so only Administrators and SYSTEM can write to it (Users: read & execute). If the " +
                      "tool needs to write there itself, install it under Program Files, or move the entry to your user PATH.",
                Scope = PathScope.Machine,
                Entries = [EntryRef.Of(entry)],
                Perspectives = Threats.Perspectives(attacker, PathScope.Machine),
                Evidence = Threats.AccessEvidence(folder, attacker, context).ToList(),
                Learn = LearnTopics.DllSearchOrder,
            };
        }
    }
}

/// <summary>SEC-02: a machine PATH folder owned by a non-admin, who can rewrite its ACL at will.</summary>
public sealed class FolderOwnedByNonAdmin : IDetector
{
    public string Rule => "SEC-02";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var entry in context.Snapshot.EntriesIn(PathScope.Machine))
        {
            var resolved = context.Resolve(entry);
            if (resolved.ViaLink || resolved.Final is not { Exists: true, OwnerSid: { } owner } folder) continue;
            if (Writability.IsAdministrativeOwner(owner)) continue;

            // When a perspective holds the owner SID, its evaluated rights say whether an OWNER RIGHTS entry has taken
            // the implicit WRITE_DAC away. Another account can't be evaluated from here, but the entry still caps it.
            var holder = new[] { folder.AccessFor(Perspective.StandardUser), folder.AccessFor(Perspective.CurrentUserUnelevated) }
                .FirstOrDefault(a => a is { OwnerInPerspective: true });
            if (holder is not null ? !holder.CanWriteDac : folder.Sddl?.Contains(";;;OW)", StringComparison.OrdinalIgnoreCase) == true)
                continue;

            var name = Writability.OwnerName(folder, context);
            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Security,
                Severity = Severity.High,
                Subject = folder.Path,
                RootCause = "owner:" + PathText.Key(folder.Path),
                Title = $"{folder.Path} is owned by {name}, not an administrator",
                What = $"{Words.Entry(entry)} is in the machine PATH and owned by {name}. A folder's owner can always change " +
                       "its permissions, whatever they currently say.",
                Why = $"So {(name == "you" ? "you (or anything running as you)" : name)} can grant write access at any time and plant files " +
                      "that SYSTEM and elevated programs will run. The permissions can look clean and still not protect the folder.",
                Fix = "Make Administrators the owner: Properties → Security → Advanced → Owner → Change, or from an elevated " +
                      $"prompt, icacls \"{folder.Path}\" /setowner Administrators. Then check the permissions.",
                Scope = PathScope.Machine,
                Entries = [EntryRef.Of(entry)],
                Perspectives = [Perspective.CurrentUserUnelevated, Perspective.StandardUser, Perspective.System],
                Evidence = [$"Owner: {name}", "No OWNER RIGHTS entry limits what the owner can do"],
                Learn = LearnTopics.DllSearchOrder,
            };
        }
    }
}

/// <summary>SEC-03: a missing machine PATH folder that a non-admin could create (a phantom directory).</summary>
public sealed class PhantomMachineFolder : IDetector
{
    public string Rule => "SEC-03";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var entry in context.Snapshot.EntriesIn(PathScope.Machine))
        {
            var resolved = context.Resolve(entry);
            if (resolved.ViaLink || resolved.Final is not { Status: ProbeStatus.Probed, Exists: false } missing) continue;
            if (context.Snapshot.FactsFor(missing.NearestExistingAncestor) is not { } ancestor) continue;

            var attacker = Threats.WhoCanCreateUnder(ancestor);
            if (attacker == Attacker.None) continue;

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Security,
                Severity = Severity.High,
                Subject = missing.Path,
                RootCause = Threats.MissingRootCause(missing),
                Title = $"{missing.Path} doesn't exist, and {Threats.Who(attacker)} could create it",
                What = $"{Words.Entry(entry)} is in the machine PATH but doesn't exist. Its nearest existing parent, " +
                       $"{ancestor.Path}, lets {Threats.Who(attacker)} create folders in it.",
                Why = "Whoever creates the missing folder decides what goes in it, and SYSTEM and elevated programs will search it. " +
                      "That's a phantom-directory hijack, and nothing looks wrong until someone uses it.",
                Fix = "Remove the entry from the machine PATH. If the software that added it is still installed, reinstall it so " +
                      "the folder exists with proper permissions.",
                Scope = PathScope.Machine,
                Entries = [EntryRef.Of(entry)],
                Perspectives = Threats.Perspectives(attacker, PathScope.Machine),
                Evidence = Threats.AccessEvidence(ancestor, attacker, context).Select(line => $"{ancestor.Path}: {line}").ToList(),
                Learn = LearnTopics.PhantomDirectories,
            };
        }
    }
}

/// <summary>SEC-04: a writable folder searched before System32, so it can shadow built-in commands.</summary>
public sealed class WritableBeforeSystem32 : IDetector
{
    public string Rule => "SEC-04";

    /// <summary>Built-ins worth naming when they could be shadowed: what people and scripts run most.</summary>
    static readonly string[] Notable =
    [
        "cmd", "powershell", "where", "net", "sc", "reg", "whoami", "ping", "tasklist", "taskkill", "schtasks",
        "msiexec", "rundll32", "regsvr32", "wmic", "curl", "tar", "ssh", "notepad", "explorer", "conhost",
    ];

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        if (context.System32Position is not { } system32) yield break;

        var builtins = context.Shadows.Builtins.Select(b => b.Command).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var named = Notable.Where(builtins.Contains).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < system32; i++)
        {
            var entry = context.Entries[i];
            if (DetectionContext.IsUnder(entry.Key, context.SystemRootKey)) continue;
            if (context.Resolve(entry).Final is not { Exists: true, IsDirectory: true } folder || !seen.Add(PathText.Key(folder.Path))) continue;

            var attacker = Threats.WhoCanPlant(folder);
            if (attacker == Attacker.None) continue;

            var severity = Threats.IsEscalation(attacker, context) ? Severity.High : Severity.Medium;
            var count = builtins.Count == 0 ? "Every Windows command" : $"{Words.Count(builtins.Count, "Windows command")}";

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Security,
                Severity = severity,
                Subject = folder.Path,
                RootCause = Threats.WritableRootCause(context, folder),
                Title = $"{folder.Path} comes before System32, and {Threats.Who(attacker)} can write to it",
                What = $"{Words.Entry(entry)} is searched before {context.Entries[system32].Expanded.Trim()}, and " +
                       $"{Threats.Who(attacker)} can add files to it. A command planted here beats the Windows one of the same name.",
                Why = "cmd and PowerShell look for a bare command folder by folder in PATH order, trying each PATHEXT extension " +
                      "within a folder, so a where.bat or where.com here runs instead of System32's where.exe. " +
                      $"{count} could be replaced this way" +
                      (named.Count > 0 ? $", including {Words.List(named)}." : "."),
                Fix = "Move the entry after the Windows folders, and lock it down so only administrators can write to it.",
                Scope = entry.Scope,
                Entries = [EntryRef.Of(entry)],
                Perspectives = Threats.Perspectives(attacker, entry.Scope),
                Evidence = Threats.AccessEvidence(folder, attacker, context).ToList(),
                Learn = LearnTopics.PathExt,
            };
        }
    }
}

/// <summary>SEC-06: a user PATH folder that other accounts can add files to (or create, when it's missing).</summary>
public sealed class UserFolderWritableByOthers : IDetector
{
    public string Rule => "SEC-06";

    const string Why = "Every program you run searches your user PATH. Another account that can write here could plant a " +
                       "command or DLL that runs as you, and as an administrator whenever you run something elevated.";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var entry in context.Snapshot.EntriesIn(PathScope.User))
        {
            var resolved = context.Resolve(entry);
            if (resolved.ViaLink || resolved.Final is not { Status: ProbeStatus.Probed } folder) continue;

            if (folder.Exists && Threats.WhoCanPlant(folder) == Attacker.AnyUser)
            {
                yield return Build(entry, folder, context,
                    title: $"Other users can add files to {folder.Path}, which is in your user PATH",
                    what: $"{Words.Entry(entry)} is in your user PATH, and any user on this PC can create files in it.",
                    fix: @"Move the tool under your profile (for example %LOCALAPPDATA%\Programs) and update the entry, or " +
                         "remove other users' write access to the folder.",
                    rootCause: Threats.WritableRootCause(context, folder),
                    evidence: Threats.AccessEvidence(folder, Attacker.AnyUser, context));
            }
            else if (!folder.Exists && context.Snapshot.FactsFor(folder.NearestExistingAncestor) is { } ancestor
                     && Threats.WhoCanCreateUnder(ancestor) == Attacker.AnyUser)
            {
                yield return Build(entry, folder, context,
                    title: $"{folder.Path} doesn't exist, and other users could create it",
                    what: $"{Words.Entry(entry)} is in your user PATH but doesn't exist, and any user can create folders in " +
                          $"its nearest existing parent, {ancestor.Path}.",
                    fix: "Remove the entry, or create the folder under your own profile and point the entry there.",
                    rootCause: Threats.MissingRootCause(folder),
                    evidence: Threats.AccessEvidence(ancestor, Attacker.AnyUser, context).Select(line => $"{ancestor.Path}: {line}"));
            }
        }
    }

    Finding Build(PathEntry entry, DirectoryFacts folder, DetectionContext context,
        string title, string what, string fix, string rootCause, IEnumerable<string> evidence) => new()
    {
        Rule = Rule,
        Category = FindingCategory.Security,
        Severity = Severity.Medium,
        Subject = folder.Path,
        RootCause = rootCause,
        Title = title,
        What = what,
        Why = Why,
        Fix = fix,
        Scope = PathScope.User,
        Entries = [EntryRef.Of(entry)],
        Perspectives = Threats.Perspectives(Attacker.AnyUser, PathScope.User),
        Evidence = evidence.ToList(),
        Learn = LearnTopics.DllSearchOrder,
    };
}

/// <summary>SEC-07: the UAC exposure summary: folders you can write unelevated that elevated sessions also search.</summary>
public sealed class UacExposure : IDetector
{
    public string Rule => "SEC-07";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        if (!context.HasSplitToken || !context.UserIsAdmin) yield break;

        var exposed = new List<(PathEntry Entry, DirectoryFacts Folder)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in context.Entries)
        {
            if (context.Resolve(entry).Final is not { Exists: true, IsDirectory: true } folder || !seen.Add(PathText.Key(folder.Path))) continue;
            if (Writability.CanPlantFiles(folder.AccessFor(Perspective.CurrentUserUnelevated))) exposed.Add((entry, folder));
        }
        if (exposed.Count == 0) yield break;

        // Windows puts WindowsApps (app execution aliases) in every user's PATH. On its own it's the baseline, not a finding.
        var onlyStock = exposed.All(e => IsWindowsApps(e.Folder));

        yield return new Finding
        {
            Rule = Rule,
            Category = FindingCategory.Security,
            Severity = onlyStock ? Severity.Info : Severity.Medium,
            Subject = "uac",
            RootCause = "uac-exposure",
            Title = $"{Words.Count(exposed.Count, "PATH folder")} you can write without elevating {(exposed.Count == 1 ? "is" : "are")} also searched by your elevated programs",
            What = "Programs you run as administrator inherit your PATH. " +
                   $"{Words.List(exposed.Select(e => e.Folder.Path))} {(exposed.Count == 1 ? "is" : "are")} writable from your normal, unelevated session.",
            Why = "Malware running as you, without admin rights, could plant a command or DLL in one of them and wait for you to " +
                  "run something elevated that searches PATH, turning an ordinary infection into an administrator one without " +
                  "a UAC prompt. Microsoft doesn't treat UAC as a security boundary, but it's the one most PCs rely on." +
                  (onlyStock ? " Here it's only the WindowsApps folder Windows adds for every user, which is the normal baseline." : ""),
            Fix = "Keep writable tool folders out of PATH where you can, or install tools for all users under Program Files. " +
                  "For admin work, a separate administrator account keeps your everyday PATH out of elevated sessions.",
            Entries = exposed.Select(e => EntryRef.Of(e.Entry)).ToList(),
            Perspectives = [Perspective.CurrentUserUnelevated, Perspective.CurrentUserElevated],
            Evidence = exposed.Select(e => $"{Words.Entry(e.Entry)}: writable by you, unelevated").ToList(),
            Learn = LearnTopics.UacAndPath,
        };
    }

    static bool IsWindowsApps(DirectoryFacts folder) =>
        PathText.Key(folder.Path).EndsWith(@"\APPDATA\LOCAL\MICROSOFT\WINDOWSAPPS", StringComparison.Ordinal);
}

/// <summary>SEC-08: an entry that's a junction or symlink to a folder someone else can write (or create).</summary>
public sealed class WritableLinkTarget : IDetector
{
    public string Rule => "SEC-08";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var entry in context.Entries)
        {
            var resolved = context.Resolve(entry);
            if (!resolved.ViaLink || resolved.Final is not { Status: ProbeStatus.Probed } target || resolved.Own is not { } link) continue;

            Attacker attacker;
            string rootCause, where;
            IEnumerable<string> evidence;
            if (target.Exists)
            {
                attacker = Threats.WhoCanPlant(target);
                rootCause = Threats.WritableRootCause(context, target);
                where = $"{target.Path}, which {Threats.Who(attacker)} can add files to";
                evidence = Threats.AccessEvidence(target, attacker, context).Select(line => $"{target.Path}: {line}");
            }
            else if (context.Snapshot.FactsFor(target.NearestExistingAncestor) is { } ancestor)
            {
                attacker = Threats.WhoCanCreateUnder(ancestor);
                rootCause = Threats.MissingRootCause(target);
                where = $"{target.Path}, which doesn't exist and {Threats.Who(attacker)} could create";
                evidence = Threats.AccessEvidence(ancestor, attacker, context).Select(line => $"{ancestor.Path}: {line}");
            }
            else continue;

            Severity severity;
            if (entry.Scope == PathScope.Machine && attacker != Attacker.None) severity = Severity.High;
            else if (entry.Scope == PathScope.User && attacker == Attacker.AnyUser) severity = Severity.Medium;
            else continue;

            var kind = link.IsSymlink ? "symbolic link" : "junction";
            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Security,
                Severity = severity,
                Subject = link.Path,
                RootCause = rootCause,
                Title = $"{link.Path} looks locked down, but it's a {kind} to a folder {Threats.Who(attacker)} can write",
                What = $"{Words.Entry(entry)} is a {kind} to {where}. Whatever lands there is what PATH finds.",
                Why = "A link can have tight permissions of its own and still hand out its target's. Checking only the entry's " +
                      "folder makes this look safe; it isn't. " +
                      (entry.Scope == PathScope.Machine
                          ? "SYSTEM and elevated programs search the machine PATH, so a planted file runs with their rights."
                          : "Every program you run searches your user PATH, so a planted file runs as you."),
                Fix = $"Point the entry at a locked-down folder, or lock down {target.Path} so only administrators and SYSTEM can write to it.",
                Scope = entry.Scope,
                Entries = [EntryRef.Of(entry)],
                Perspectives = Threats.Perspectives(attacker, entry.Scope),
                Evidence = [.. resolved.Links.Select(l => $"{l.Path} → {l.ReparseTarget}"), .. evidence],
                Learn = LearnTopics.DllSearchOrder,
            };
        }
    }
}

/// <summary>SEC-09: entries on a UNC share, a mapped or removable drive, an unmounted letter, or behind a link to the network.</summary>
public sealed class FragileLocation : IDetector
{
    public string Rule => "SEC-09";

    enum Kind { Unc, Mapped, LinkToNetwork, Removable, Unmounted }

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        var linked = context.Snapshot.Host.EnableLinkedConnections == true;
        foreach (var entry in context.Entries)
        {
            var own = context.Resolve(entry).Own;
            Kind? kind =
                entry.Form == PathForm.Unc || own?.Drive == DriveKind.Unc ? Kind.Unc
                : own?.Drive == DriveKind.MappedNetwork ? Kind.Mapped
                : own?.ReparseTargetIsNetwork == true ? Kind.LinkToNetwork
                : own?.Drive is DriveKind.Removable or DriveKind.CdRom ? Kind.Removable
                : own?.Drive == DriveKind.NoRootDir ? Kind.Unmounted
                : null;
            if (kind is not { } k) continue;

            var path = entry.ProbePath ?? entry.Expanded.Trim();
            var host = PathText.RootOf(k == Kind.LinkToNetwork ? own!.ReparseTarget ?? path : own?.DriveTarget ?? path)?.TrimEnd('\\') ?? path;
            var machine = entry.Scope == PathScope.Machine;
            var (title, contexts, fix) = k switch
            {
                Kind.Unc or Kind.LinkToNetwork => (
                    k == Kind.Unc ? $"{path} is on a network share" : $"{path} is a link to a network share",
                    new List<string>
                    {
                        $"Every program that falls through to this entry contacts {host} and signs in to it with your Windows credentials" +
                        (machine ? " (or, for services, the computer's own account)." : "."),
                        $"Whoever controls {host}, or can answer for its name on your network, chooses what runs.",
                        "When the share is slow or unreachable, every command that isn't found earlier in PATH stalls.",
                    },
                    "Copy the tools to a local folder" + (machine ? " under Program Files" : "") + " and point the entry there."),
                Kind.Mapped => (
                    $"{path} is on a mapped network drive",
                    new List<string>
                    {
                        $"{PathText.RootOf(path)?.TrimEnd('\\')} maps to {host}. Mappings belong to your sign-in session, so SYSTEM and services never see it: for them the entry is missing.",
                        linked
                            ? "EnableLinkedConnections is set, so your elevated sessions share the mapping."
                            : "Elevated sessions don't see it either, because EnableLinkedConnections isn't set.",
                        $"Whoever controls {host} chooses what runs, and each lookup signs in to it with your credentials.",
                    },
                    "Copy the tools to a local folder and point the entry there."),
                Kind.Removable => (
                    $"{path} is on a removable drive",
                    new List<string>
                    {
                        $"Whoever puts media in {PathText.RootOf(path)?.TrimEnd('\\')} controls what runs from this entry.",
                        "With the drive empty or unplugged, the entry is dead.",
                    },
                    "Install the tools on a fixed drive and point the entry there."),
                _ => (
                    $"{path} is on {PathText.RootOf(path)?.TrimEnd('\\')}, which isn't mounted",
                    new List<string>
                    {
                        $"Nothing is mounted at {PathText.RootOf(path)?.TrimEnd('\\')} right now, so the entry is dead.",
                        "If a USB drive, or a mapped or subst drive, ever takes that letter, it supplies this entry's commands.",
                    },
                    "Remove the entry, or reconnect the drive it belongs to."),
            };

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Security,
                Severity = Severity.Medium,
                Subject = path,
                RootCause = k == Kind.Unmounted && context.Resolve(entry).Final is { } gone ? Threats.MissingRootCause(gone) : "location:" + entry.Key,
                Title = title,
                What = $"{Words.Entry(entry)} lives somewhere that isn't always there, and isn't always yours.",
                Why = string.Join(" ", contexts),
                Fix = fix,
                Scope = entry.Scope,
                Entries = [EntryRef.Of(entry)],
                Perspectives = machine
                    ? [Perspective.System, Perspective.CurrentUserElevated, Perspective.CurrentUserUnelevated]
                    : [Perspective.CurrentUserUnelevated, Perspective.CurrentUserElevated],
                Evidence = contexts,
                Learn = LearnTopics.NewProcessPath,
            };
        }
    }
}
