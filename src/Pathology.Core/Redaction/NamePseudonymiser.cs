using System.Text.RegularExpressions;
using Pathology.Core.Detection;
using Pathology.Core.Model;

namespace Pathology.Core.Redaction;

/// <summary>
/// The second half of redaction: names that aren't personal data on their own, but say more about a person or an
/// employer than a bug report needs. A folder of scripts called <c>acme.deploy.bat</c> and <c>payroll.api.bat</c>,
/// or a variable called <c>ACME_TOKEN_FILE</c>, names the projects someone works on.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Command file names</b> outside the Windows folder become <c>&lt;cmd-n&gt;</c>, keeping the extension.
/// The same name (case-insensitively, by name without extension) always gets the same number, so which folder
/// wins, which copies are hidden and which tools compete all diagnose exactly as before. A name that the Windows
/// folder also provides stays readable everywhere: Windows' own commands aren't private, and keeping them is what
/// lets "C:\Tools\where.bat shadows where.exe" survive.</item>
/// <item><b>Environment variable names</b> become <c>&lt;var-n&gt;</c> unless their value was captured (PATH
/// depends on them), a PATH entry names them, or they're a well-known Windows variable.</item>
/// </list>
/// Placeholders contain <c>&lt;</c>, which no file or variable name can, so a second pass leaves them alone.
/// </remarks>
internal static class NamePseudonymiser
{
    static readonly Regex CommandPlaceholder = new(@"^<cmd-\d+>$", RegexOptions.CultureInvariant);
    static readonly Regex VariablePlaceholder = new(@"^<var-\d+>$", RegexOptions.CultureInvariant);

    /// <summary>Variables every Windows logon has: naming them says nothing about anyone.</summary>
    static readonly HashSet<string> WellKnownVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALLUSERSPROFILE", "APPDATA", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "COMPUTERNAME", "ComSpec", "DriverData", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "LOGONSERVER",
        "NUMBER_OF_PROCESSORS", "OneDrive", "OneDriveCommercial", "OneDriveConsumer", "OS", "Path", "PATHEXT",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "PROMPT", "PSModulePath", "PUBLIC", "SESSIONNAME",
        "SystemDrive", "SystemRoot", "TEMP", "TMP", "USERDOMAIN", "USERDOMAIN_ROAMINGPROFILE", "USERNAME",
        "USERPROFILE", "windir", "NoDefaultCurrentDirectoryInExePath", "__COMPAT_LAYER",
    };

    public static PathSnapshot Apply(PathSnapshot snapshot) => snapshot with
    {
        Directories = CommandNames(snapshot),
        Environment = VariableNames(snapshot),
    };

    static IReadOnlyList<DirectoryFacts> CommandNames(PathSnapshot snapshot)
    {
        var systemRoot = DetectionContext.KeyOf(
            snapshot.EnvironmentFrom(EnvironmentSource.NewProcess)?.ValueOf("SystemRoot")
            ?? snapshot.EnvironmentFrom(EnvironmentSource.Machine)?.ValueOf("SystemRoot")
            ?? @"C:\Windows");
        bool InWindows(DirectoryFacts d) => DetectionContext.IsUnder(DetectionContext.KeyOf(d.Path), systemRoot);

        // What Windows itself provides, by name without extension: those stay readable wherever they appear.
        var windows = snapshot.Directories.Where(InWindows)
            .SelectMany(d => d.CommandFiles ?? [])
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in snapshot.Directories.SelectMany(d => d.CommandFiles ?? []).Select(Path.GetFileNameWithoutExtension))
            if (name is not null && CommandPlaceholder.IsMatch(name)) numbers[name] = int.Parse(name[5..^1]);
        var next = numbers.Count == 0 ? 1 : numbers.Values.Max() + 1;

        string Rename(string file)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (windows.Contains(stem) || CommandPlaceholder.IsMatch(stem)) return file;
            if (!numbers.TryGetValue(stem, out var n)) numbers[stem] = n = next++;
            return $"<cmd-{n}>{Path.GetExtension(file)}";
        }

        return snapshot.Directories
            .Select(d => d.CommandFiles is { } files && !InWindows(d) ? d with { CommandFiles = files.Select(Rename).ToList() } : d)
            .ToList();
    }

    static IReadOnlyList<EnvironmentVariables> VariableNames(PathSnapshot snapshot)
    {
        // Kept: anything with a captured value somewhere, and anything a PATH entry names (defined or not).
        var keep = new HashSet<string>(WellKnownVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var set in snapshot.Environment)
            foreach (var (name, value) in set.Variables)
                if (value is not null) keep.Add(name);
        foreach (var entry in snapshot.Entries)
            keep.UnionWith(entry.Variables);

        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in snapshot.Environment.SelectMany(e => e.Variables.Keys).Where(n => VariablePlaceholder.IsMatch(n)))
            numbers[name] = int.Parse(name[5..^1]);
        var next = numbers.Count == 0 ? 1 : numbers.Values.Max() + 1;

        string Rename(string name)
        {
            if (keep.Contains(name) || VariablePlaceholder.IsMatch(name)) return name;
            if (!numbers.TryGetValue(name, out var n)) numbers[name] = n = next++;
            return $"<var-{n}>";
        }

        return snapshot.Environment
            .Select(set => set with
            {
                Variables = set.Variables
                    .Select(p => (Name: Rename(p.Key), p.Value))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase),
            })
            .ToList();
    }
}
