using Pathology.Core.Detection;
using Pathology.Core.Model;

namespace Pathology.Core.Shadowing;

/// <summary>One file on PATH that a command name could run.</summary>
/// <param name="Command">The bare command name, as typed (<c>python</c>).</param>
/// <param name="FileName">The file's name as captured (<c>python.exe</c>).</param>
/// <param name="Folder">The folder, as the PATH entry names it.</param>
/// <param name="Entry">The first PATH entry naming that folder.</param>
/// <param name="Position">That entry's place in the search order (machine then user, from 0).</param>
/// <param name="WritableByOthers">Any user on this PC could replace it (the standard-user perspective can add files there).</param>
/// <param name="WritableByYou">Your unelevated session could replace it.</param>
/// <param name="IsWindows">The folder is inside the Windows directory (a built-in command).</param>
public sealed record CommandProvider(
    string Command, string FileName, string Folder, EntryRef Entry, int Position,
    bool WritableByOthers, bool WritableByYou, bool IsWindows)
{
    public string Extension => System.IO.Path.GetExtension(FileName).ToUpperInvariant();
    public string FullPath => Folder.TrimEnd('\\') + @"\" + FileName;
}

/// <summary>How one command name resolves: who wins, who's hidden, and where other shells differ.</summary>
public sealed record CommandResolution(
    string Command, CommandProvider Winner, IReadOnlyList<CommandProvider> Hidden, IReadOnlyList<string> Notes)
{
    /// <summary>Hidden copies in a different folder from the winner (not just a lower-precedence extension beside it).</summary>
    public IEnumerable<CommandProvider> HiddenElsewhere =>
        Hidden.Where(h => !string.Equals(h.Folder, Winner.Folder, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Every command PATH provides, resolved the way cmd does: folder by folder in PATH order, trying each
/// <c>PATHEXT</c> extension in turn within a folder. Built from the file names captured in the snapshot.
/// </summary>
/// <remarks>
/// cmd also looks in the current directory before PATH (unless <c>NoDefaultCurrentDirectoryInExePath</c> is
/// set); that isn't a property of PATH, so it isn't modelled. PowerShell's differences are notes, not rules.
/// </remarks>
public sealed class ShadowReport
{
    readonly Dictionary<string, CommandResolution> _byName;

    internal ShadowReport(IReadOnlyList<CommandResolution> all, IReadOnlyList<string> unlisted, IReadOnlyList<string> pathExt)
    {
        All = all;
        UnlistedFolders = unlisted;
        PathExt = pathExt;
        _byName = all.ToDictionary(r => r.Command, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every command name, alphabetically.</summary>
    public IReadOnlyList<CommandResolution> All { get; }

    /// <summary>Commands more than one folder provides, riskiest first (a writable winner), then by name.</summary>
    public IEnumerable<CommandResolution> Competing => All
        .Where(r => r.HiddenElsewhere.Any())
        .OrderByDescending(r => r.Winner.WritableByOthers)
        .ThenByDescending(r => r.Winner.WritableByYou)
        .ThenBy(r => r.Command, StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders on PATH whose files weren't captured (missing, network, unreadable), so a lookup may miss them.</summary>
    public IReadOnlyList<string> UnlistedFolders { get; }

    public IReadOnlyList<string> PathExt { get; }

    /// <summary>Commands whose winner is a built-in in the Windows directory.</summary>
    public IEnumerable<CommandResolution> Builtins => All.Where(r => r.Winner.IsWindows);

    /// <summary>
    /// How a name resolves ("which python?"). A name with a <c>PATHEXT</c> extension (<c>python.exe</c>) matches
    /// that file only, as cmd does. Null when nothing on PATH provides it.
    /// </summary>
    public CommandResolution? Resolve(string name)
    {
        name = name.Trim();
        if (_byName.TryGetValue(name, out var bare)) return bare;

        var ext = System.IO.Path.GetExtension(name);
        if (ext.Length == 0 || !_byName.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(name), out var byBase)) return null;
        var matches = new[] { byBase.Winner }.Concat(byBase.Hidden)
            .Where(p => p.FileName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 0 ? null : byBase with { Winner = matches[0], Hidden = matches.Skip(1).ToList() };
    }
}
