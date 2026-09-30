using Pathology.Core.Detection;
using Pathology.Core.Model;

namespace Pathology.Core.Shadowing;

/// <summary>Builds a <see cref="ShadowReport"/> from the command files captured for each PATH folder.</summary>
public static class ShadowEngine
{
    /// <summary>
    /// Windows PowerShell's built-in aliases that share a name with a common program, so typing the name in
    /// PowerShell never reaches PATH. (PowerShell 7 dropped the ones marked 5.1.)
    /// </summary>
    static readonly Dictionary<string, string> PowerShellAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["where"] = "Where-Object",
        ["sc"] = "Set-Content (Windows PowerShell 5.1)",
        ["curl"] = "Invoke-WebRequest (Windows PowerShell 5.1)",
        ["wget"] = "Invoke-WebRequest (Windows PowerShell 5.1)",
        ["sort"] = "Sort-Object (Windows PowerShell 5.1)",
        ["diff"] = "Compare-Object (Windows PowerShell 5.1)",
        ["tee"] = "Tee-Object",
        ["fc"] = "Format-Custom",
        ["kill"] = "Stop-Process",
        ["ps"] = "Get-Process",
        ["cat"] = "Get-Content",
        ["type"] = "Get-Content",
        ["echo"] = "Write-Output",
        ["ls"] = "Get-ChildItem",
        ["dir"] = "Get-ChildItem",
        ["man"] = "help",
        ["start"] = "Start-Process",
        ["mv"] = "Move-Item",
        ["cp"] = "Copy-Item",
        ["rm"] = "Remove-Item",
        ["rmdir"] = "Remove-Item",
        ["mount"] = "New-PSDrive (Windows PowerShell 5.1)",
        ["r"] = "Invoke-History",
        ["h"] = "Get-History",
    };

    public static ShadowReport Build(DetectionContext context)
    {
        var pathExt = context.PathExt;
        var precedence = pathExt.Select((e, i) => (e, i)).ToDictionary(p => p.e, p => p.i, StringComparer.OrdinalIgnoreCase);

        // Each folder once, at its first position in the search order.
        var folders = new List<(PathEntry Entry, int Position, IReadOnlyList<string> Files, bool Others, bool You, bool Windows)>();
        var unlisted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < context.Entries.Count; i++)
        {
            var entry = context.Entries[i];
            if (entry.ProbePath is null || !seen.Add(entry.Key)) continue;

            var resolved = context.Resolve(entry);
            if (resolved.Own?.CommandFiles is not { } files)
            {
                if (resolved.Final is not { Status: ProbeStatus.Probed, Exists: false }) unlisted.Add(entry.ProbePath);
                continue;
            }
            folders.Add((entry, i, files,
                Writability.CanPlantFiles(resolved.Final?.AccessFor(Perspective.StandardUser)),
                Writability.CanPlantFiles(resolved.Final?.AccessFor(Perspective.CurrentUserUnelevated)),
                DetectionContext.IsUnder(entry.Key, context.SystemRootKey)));
        }

        // command → candidates in resolution order: folder order first, then PATHEXT order within a folder.
        var providers = new Dictionary<string, List<(int Folder, int Ext, CommandProvider Provider)>>(StringComparer.OrdinalIgnoreCase);
        var scripts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var f = 0; f < folders.Count; f++)
        {
            var folder = folders[f];
            foreach (var file in folder.Files)
            {
                var command = Path.GetFileNameWithoutExtension(file);
                var ext = Path.GetExtension(file);
                if (command.Length == 0) continue;
                if (!precedence.TryGetValue(ext, out var rank))
                {
                    if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                        (scripts.TryGetValue(command, out var list) ? list : scripts[command] = []).Add(folder.Entry.ProbePath!.TrimEnd('\\') + @"\" + file);
                    continue;
                }
                var provider = new CommandProvider(command, file, folder.Entry.ProbePath!, EntryRef.Of(folder.Entry), folder.Position,
                    folder.Others, folder.You, folder.Windows);
                (providers.TryGetValue(command, out var candidates) ? candidates : providers[command] = []).Add((f, rank, provider));
            }
        }

        var all = providers
            .Select(p =>
            {
                var ordered = p.Value.OrderBy(c => c.Folder).ThenBy(c => c.Ext).Select(c => c.Provider).ToList();
                return new CommandResolution(ordered[0].Command, ordered[0], ordered.Skip(1).ToList(), Notes(p.Key, scripts));
            })
            .OrderBy(r => r.Command, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ShadowReport(all, unlisted, pathExt);
    }

    static IReadOnlyList<string> Notes(string command, Dictionary<string, List<string>> scripts)
    {
        var notes = new List<string>();
        if (PowerShellAliases.TryGetValue(command, out var alias))
            notes.Add($"In PowerShell, {command} is an alias for {alias}, which runs instead of anything on PATH.");
        if (scripts.TryGetValue(command, out var ps1))
            notes.Add($"PowerShell can also run {Words.List(ps1)}, which cmd ignores. PowerShell checks aliases, functions and cmdlets before PATH.");
        return notes;
    }
}
