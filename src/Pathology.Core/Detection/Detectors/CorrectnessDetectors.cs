using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Core.Detection.Detectors;

/// <summary>
/// COR-01: a <c>%VAR%</c> in an expanding value that stays literal: the machine PATH using a variable only the
/// user defines (it's expanded before user variables exist), or a variable nothing defines.
/// </summary>
public sealed class UserOnlyVariableInMachinePath : IDetector
{
    public string Rule => "COR-01";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var entry in context.Entries)
        {
            if (!context.Snapshot.PathFor(entry.Scope).Expands || entry.UnresolvedVariables.Count == 0) continue;

            var userOnly = entry.Scope == PathScope.Machine
                ? entry.UnresolvedVariables.Where(v => context.Defines(EnvironmentSource.User, v)
                    && !context.Defines(EnvironmentSource.Machine, v) && !context.Defines(EnvironmentSource.Volatile, v)).ToList()
                : [];
            var undefined = entry.UnresolvedVariables.Except(userOnly, StringComparer.OrdinalIgnoreCase)
                .Where(v => !Enum.GetValues<EnvironmentSource>().Any(s => context.Defines(s, v)))
                .ToList();
            if (userOnly.Count == 0 && undefined.Count == 0) continue;

            var names = Words.List(userOnly.Concat(undefined).Select(v => $"%{v}%"));
            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Correctness,
                Severity = Severity.High,
                Subject = $"{Words.Scope(entry.Scope)}#{entry.Index}",
                RootCause = $"entry:{entry.Scope}:{entry.Index}",
                Title = userOnly.Count > 0
                    ? $"The machine PATH uses {names}, which only your user environment defines"
                    : $"{names} in {Words.Path(entry.Scope)} isn't defined, so the entry stays literal",
                What = $"\"{entry.Raw.Trim()}\" ({Words.Scope(entry.Scope)} #{entry.Index + 1}) refers to {names}. " +
                       (userOnly.Count > 0
                           ? "The machine PATH is expanded before user variables exist, so "
                           : "Nothing defines it, so ") +
                       $"every process gets the literal text \"{entry.Expanded.Trim()}\".",
                Why = "A literal %…% entry is a relative path: Windows looks for a folder with that odd name under whatever the " +
                      "current directory is. The tools you meant to add aren't found, and a folder by that name in a directory " +
                      "you work in would be searched instead.",
                Fix = userOnly.Count > 0
                    ? $"Define {Words.List(userOnly)} as a system variable, move the entry to your user PATH, or write the folder out in full."
                    : $"Define {Words.List(undefined)}, or replace the reference with the folder it should point to.",
                Scope = entry.Scope,
                Entries = [EntryRef.Of(entry)],
                Perspectives = entry.Scope == PathScope.Machine ? [Perspective.System, Perspective.CurrentUserUnelevated] : [Perspective.CurrentUserUnelevated],
                Evidence = [.. userOnly.Select(v => $"%{v}% is defined only in HKCU\\Environment"), .. undefined.Select(v => $"%{v}% isn't defined at any scope")],
                Learn = LearnTopics.NewProcessPath,
            };
        }
    }
}

/// <summary>COR-02: <c>%VAR%</c> references inside a <c>REG_SZ</c> value, which Windows never expands.</summary>
public sealed class VariableInRegSz : IDetector
{
    public string Rule => "COR-02";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var scope in new[] { PathScope.Machine, PathScope.User })
        {
            if (context.Snapshot.PathFor(scope).Kind != PathValueKind.String) continue;
            var entries = context.Snapshot.EntriesIn(scope).Where(e => e.Variables.Count > 0).ToList();
            if (entries.Count == 0) continue;

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Correctness,
                Severity = Severity.High,
                Subject = Words.Scope(scope),
                RootCause = "kind:" + scope,
                Title = $"{Capitalise(Words.Path(scope))} is stored as REG_SZ, so its %variables% are never expanded",
                What = $"{Capitalise(Words.Path(scope))} is a plain string (REG_SZ), but {Words.Count(entries.Count, "entry", "entries")} " +
                       $"use %…% references: {Words.List(entries.Select(e => $"\"{e.Raw.Trim()}\""))}. Windows expands them only in " +
                       "REG_EXPAND_SZ values, so they stay literal.",
                Why = "Each of those entries reaches every process as literal text like %SystemRoot%\\system32: a relative path " +
                      "that finds nothing, or finds whatever folder by that name sits in the current directory.",
                Fix = "Change the value's type to REG_EXPAND_SZ, keeping its text exactly as it is. Tools that rewrite PATH as a " +
                      "plain string, setx among them, are the usual cause.",
                Scope = scope,
                Entries = entries.Select(EntryRef.Of).ToList(),
                Perspectives = [Perspective.CurrentUserUnelevated, Perspective.System],
                Evidence = entries.Select(e => $"{Words.Scope(scope)} #{e.Index + 1}: {e.Raw.Trim()}").ToList(),
                Learn = LearnTopics.ValueKinds,
            };
        }
    }

    internal static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// COR-03: relative entries (<c>.</c>, <c>bin</c>, <c>C:tools</c>, <c>\tools</c>), which depend on the
/// current directory, and empty ones, which some programs read as the current directory.
/// </summary>
public sealed class RelativeOrEmptyEntry : IDetector
{
    public string Rule => "COR-03";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var entry in context.Entries)
        {
            // A literal %VAR% is relative too, but COR-01 and COR-02 explain why it's literal.
            if (entry.Form is not (PathForm.Relative or PathForm.DriveRelative or PathForm.RootRelative) || entry.Variables.Count > 0) continue;

            var text = PathText.Strip(entry.Raw);
            var meaning = entry.Form switch
            {
                PathForm.DriveRelative => $"\"{text}\" means {text[2..]} under drive {text[..2]}'s current directory",
                PathForm.RootRelative => $"\"{text}\" means {text} at the root of whichever drive is current",
                _ => text is "." or ".\\" ? "\".\" is the current directory itself" : $"\"{text}\" is looked up under the current directory",
            };

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Correctness,
                Severity = Severity.High,
                Subject = $"{Words.Scope(entry.Scope)}#{entry.Index}",
                RootCause = $"entry:{entry.Scope}:{entry.Index}",
                Title = $"\"{text}\" in {Words.Path(entry.Scope)} is relative, so it depends on the current directory",
                What = $"{Words.Entry(entry)} isn't a full path: {meaning}.",
                Why = "What a relative entry finds depends on where each program happens to be started, so any folder you open a " +
                      "prompt in, or a program sets as its working directory, can supply commands and DLLs.",
                Fix = "Replace it with the full path it's meant to be, or remove it.",
                Scope = entry.Scope,
                Entries = [EntryRef.Of(entry)],
                Perspectives = [Perspective.CurrentUserUnelevated, Perspective.CurrentUserElevated, Perspective.System],
                Learn = LearnTopics.DllSearchOrder,
            };
        }

        foreach (var scope in new[] { PathScope.Machine, PathScope.User })
        {
            // The single trailing ";" is left alone: Windows writes one itself (a new user's PATH ends with it), and
            // at the very end an empty entry can only be searched after everything else.
            var empty = context.Snapshot.EntriesIn(scope)
                .Where(e => e.Form == PathForm.Empty && (e.Defects & HygieneDefects.TrailingSeparator) == 0)
                .ToList();
            if (empty.Count == 0) continue;

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Correctness,
                Severity = Severity.Medium,
                Subject = Words.Scope(scope) + "#empty",
                RootCause = "empty:" + scope,
                Title = $"{VariableInRegSz.Capitalise(Words.Path(scope))} has {Words.Count(empty.Count, "empty entry", "empty entries")}",
                What = $"Doubled or leading semicolons leave empty entries, at {Words.List(empty.Select(e => $"#{e.Index + 1}"))}.",
                Why = "Windows skips them, but tools that translate PATH for a Unix-style shell (Git Bash, MSYS2, Cygwin) can turn " +
                      "an empty entry into the current directory, so in those shells a command in whatever folder you're in is " +
                      "found before the entries that follow.",
                Fix = "Remove the extra semicolons.",
                Scope = scope,
                Entries = empty.Select(EntryRef.Of).ToList(),
                Perspectives = [Perspective.CurrentUserUnelevated],
            };
        }
    }
}

/// <summary>COR-04: machine PATH entries inside a user profile.</summary>
public sealed class ProfilePathInMachinePath : IDetector
{
    public string Rule => "COR-04";

    static readonly string[] ProfileVariables = ["USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEPATH", "USERNAME", "OneDrive"];
    static readonly string[] SharedProfiles = ["PUBLIC", "DEFAULT", "DEFAULT USER", "ALL USERS"];

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var entry in context.Snapshot.EntriesIn(PathScope.Machine))
        {
            var variable = entry.Variables.FirstOrDefault(v => ProfileVariables.Contains(v, StringComparer.OrdinalIgnoreCase));
            var inProfile = InProfile(entry.Key, context);
            if (variable is null && inProfile is null) continue;

            var whose = context.UserProfileKey is { } mine && DetectionContext.IsUnder(entry.Key, mine) ? "your" : "another user's";
            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Correctness,
                Severity = Severity.Medium,
                Subject = $"machine#{entry.Index}",
                RootCause = $"entry:{PathScope.Machine}:{entry.Index}",
                Title = $"{Words.Display(entry)} is inside a user profile but is in the machine PATH",
                What = variable is not null
                    ? $"{Words.Entry(entry)} uses %{variable}%, which points at a different profile for every account, SYSTEM included."
                    : $"{Words.Entry(entry)} points into {whose} profile.",
                Why = "Every account searches the machine PATH, SYSTEM and services included. For anyone else the folder is " +
                      "missing or belongs to someone else, and SYSTEM ends up searching a folder its owner controls.",
                Fix = "Move the entry to the user PATH of the account that uses it.",
                Scope = PathScope.Machine,
                Entries = [EntryRef.Of(entry)],
                Perspectives = [Perspective.System, Perspective.StandardUser],
            };
        }
    }

    static string? InProfile(string key, DetectionContext context)
    {
        var root = context.ProfilesRootKey.TrimEnd('\\') + @"\";
        if (!key.StartsWith(root, StringComparison.Ordinal)) return null;
        var name = key[root.Length..].Split('\\')[0];
        return name.Length == 0 || SharedProfiles.Contains(name) ? null : name;
    }
}

/// <summary>COR-05: entries that don't exist, or name a file rather than a folder.</summary>
public sealed class DeadEntry : IDetector
{
    public string Rule => "COR-05";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var entry in context.Entries)
        {
            var resolved = context.Resolve(entry);
            if (resolved.Final is not { Status: ProbeStatus.Probed } folder) continue;
            if (folder.Exists && folder.IsDirectory) continue;

            var viaLink = resolved.ViaLink ? $" (it's a link to {folder.Path})" : "";
            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Correctness,
                Severity = Severity.Low,
                Subject = folder.Path,
                RootCause = Threats.MissingRootCause(folder),
                Title = folder.Exists ? $"{folder.Path} is a file, not a folder" : $"{folder.Path} doesn't exist",
                What = folder.Exists
                    ? $"{Words.Entry(entry)} names a file{viaLink}. PATH entries must be folders."
                    : $"{Words.Entry(entry)} points at a folder that isn't there{viaLink}.",
                Why = folder.Exists
                    ? "Windows can't search inside a file, so the entry does nothing but lengthen PATH."
                    : "Every command lookup that gets this far checks it and finds nothing, and it leaves a slot that whoever " +
                      "creates the folder later gets to fill.",
                Fix = "Remove the entry, or reinstall whatever should have created the folder.",
                Scope = entry.Scope,
                Entries = [EntryRef.Of(entry)],
                Perspectives = [Perspective.CurrentUserUnelevated],
                Learn = folder.Exists ? null : LearnTopics.PhantomDirectories,
            };
        }
    }
}

/// <summary>COR-06: entries that name the same folder once expanded and tidied.</summary>
public sealed class DuplicateEntry : IDetector
{
    public string Rule => "COR-06";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        var groups = context.Entries
            .Where(e => e.Form is PathForm.Absolute or PathForm.Unc or PathForm.DevicePath)
            .GroupBy(e => e.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var copies = group.ToList();
            var first = copies[0];
            var crossScope = copies.Any(c => c.Scope != first.Scope);
            var identical = copies.All(c => c.Raw.Trim().Equals(first.Raw.Trim(), StringComparison.Ordinal));

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Correctness,
                Severity = Severity.Low,
                Subject = group.Key,
                RootCause = "dup:" + group.Key,
                Title = $"{Words.Display(first)} is in PATH {copies.Count} times",
                What = (identical ? "These entries are identical: " : "These entries all name the same folder once expanded and tidied: ") +
                       $"{Words.List(copies.Select(Words.Entry))}. Only the first is ever used.",
                Why = "The extra copies lengthen PATH and make it harder to read, and it's easy to edit one copy and miss the others." +
                      (crossScope ? " A copy in your user PATH of a machine entry does nothing at all: the machine PATH is searched first." : ""),
                Fix = $"Keep {Words.Entry(first)} and remove the rest.",
                Scope = crossScope ? null : first.Scope,
                Entries = copies.Select(EntryRef.Of).ToList(),
                Perspectives = [Perspective.CurrentUserUnelevated],
                Evidence = copies.Select(c => $"{Words.Scope(c.Scope)} #{c.Index + 1}: {c.Raw}").ToList(),
            };
        }
    }
}

/// <summary>COR-07: folders that provide the same commands, and which of them wins (the Shadowing page's input).</summary>
public sealed class CompetingExecutables : IDetector
{
    public string Rule => "COR-07";

    const int MaxNamed = 8;
    const int MaxEvidence = 25;

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        var pairs = context.Shadows.Competing
            .SelectMany(r => r.HiddenElsewhere
                .DistinctBy(h => h.Folder, StringComparer.OrdinalIgnoreCase)
                .Select(h => (Winner: r.Winner, Hidden: h)))
            .GroupBy(p => (Winner: p.Winner.Folder.ToUpperInvariant(), Hidden: p.Hidden.Folder.ToUpperInvariant()))
            .OrderBy(g => g.First().Winner.Position)
            .ThenBy(g => g.First().Hidden.Position);

        foreach (var pair in pairs)
        {
            var winner = pair.First().Winner;
            var hidden = pair.First().Hidden;
            var commands = pair.Select(p => p.Winner.Command).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var named = Words.List(commands.Take(MaxNamed)) + (commands.Count > MaxNamed ? $" and {commands.Count - MaxNamed} more" : "");

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Correctness,
                Severity = Severity.Info,
                Subject = $"{winner.Folder}>{hidden.Folder}",
                RootCause = $"shadow:{pair.Key.Winner}>{pair.Key.Hidden}",
                Title = $"{winner.Folder} hides {Words.Count(commands.Count, "command")} that {hidden.Folder} also has",
                What = $"Both folders provide {named}. {winner.Folder} comes first in PATH, so its copies run; the ones in " +
                       $"{hidden.Folder} are only reachable by their full path.",
                Why = "Which version of a tool you get depends only on PATH order. That's often intended (a newer runtime ahead of " +
                      "an older one), but it's also where \"works on my machine\" comes from.",
                Fix = "If the order isn't what you meant, reorder the entries. If one folder is left over from an old install, remove it.",
                Entries = [winner.Entry, hidden.Entry],
                Perspectives = [Perspective.CurrentUserUnelevated],
                Evidence = pair.Take(MaxEvidence).Select(p => $"{p.Winner.Command}: {p.Winner.FullPath} wins over {p.Hidden.FullPath}").ToList(),
                Learn = LearnTopics.PathExt,
            };
        }
    }
}

/// <summary>COR-08: PATH length against the limits that break things, and the setx 1024-character truncation.</summary>
public sealed class LengthHeadroom : IDetector
{
    public string Rule => "COR-08";

    public const int LegacyLimit = 2047;
    public const int VariableLimit = 32767;
    public const int SetxLimit = 1024;

    static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The PATH a new process gets, or the two stored values joined when that wasn't captured.</summary>
    public static string EffectivePath(PathSnapshot snapshot) =>
        snapshot.EffectivePath ?? string.Join(';', new[] { snapshot.MachinePath.Value, snapshot.UserPath.Value }.OfType<string>());

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        var effective = EffectivePath(context.Snapshot);

        foreach (var (limit, what) in new[]
                 {
                     (LegacyLimit, "the 2,047 characters that older programs and the old Environment Variables dialog handle"),
                     (VariableLimit, "the 32,767-character limit on any environment variable"),
                 })
        {
            var ratio = effective.Length / (double)limit;
            if (ratio < 0.8) continue;

            var over = effective.Length > limit;
            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Correctness,
                Severity = ratio >= 0.95 ? Severity.High : Severity.Medium,
                Subject = N(limit),
                RootCause = "length:" + limit,
                Title = over
                    ? $"The effective PATH is {N(effective.Length)} characters, past the {N(limit)}-character limit"
                    : $"The effective PATH is {N(effective.Length)} characters, {(int)(ratio * 100)}% of the {N(limit)}-character limit",
                What = $"A new process's PATH is {N(effective.Length)} characters long, against {what}. " +
                       (over ? $"It's {N(effective.Length - limit)} over." : $"That leaves {N(limit - effective.Length)} to spare."),
                Why = limit == LegacyLimit
                    ? "Past this point, older installers and tools truncate PATH or fail to read it, and a truncated PATH written " +
                      "back loses whatever was at the end."
                    : "Windows can't hold a longer variable: entries beyond it are lost, and programs that append to PATH fail.",
                Fix = "Remove dead and duplicate entries first. Then shorten long entries: a variable like %ProgramFiles% or a " +
                      "short folder with links in it can stand in for many long paths.",
                Perspectives = [Perspective.CurrentUserUnelevated],
                Evidence =
                [
                    $"Machine PATH: {N(context.Snapshot.MachinePath.Length)} characters stored",
                    $"User PATH: {N(context.Snapshot.UserPath.Length)} characters stored",
                    $"New-process PATH: {N(effective.Length)} characters",
                ],
                Learn = LearnTopics.NewProcessPath,
            };
        }

        foreach (var scope in new[] { PathScope.Machine, PathScope.User })
        {
            if (context.Snapshot.PathFor(scope).Length != SetxLimit) continue;
            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Correctness,
                Severity = Severity.High,
                Subject = "setx:" + Words.Scope(scope),
                RootCause = "setx:" + scope,
                Title = $"{VariableInRegSz.Capitalise(Words.Path(scope))} is exactly 1,024 characters: it looks truncated by setx",
                What = $"{VariableInRegSz.Capitalise(Words.Path(scope))} is stored at exactly 1,024 characters, the length setx " +
                       "silently cuts values to.",
                Why = "If setx wrote it, everything past the 1,024th character is gone, and the last entry is probably cut in half.",
                Fix = "Compare it with a backup, or with what your installed tools expect, and restore the missing entries. Don't " +
                      "use setx to edit PATH: edit it in the Environment Variables dialog instead.",
                Scope = scope,
                Entries = context.Snapshot.EntriesIn(scope).TakeLast(1).Select(EntryRef.Of).ToList(),
                Perspectives = [Perspective.CurrentUserUnelevated],
                Learn = LearnTopics.WhyNotSetx,
            };
        }
    }
}
