using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Core.Capture;

/// <summary>
/// Decides which environment variable <b>values</b> a snapshot keeps. Names are always kept. Values are kept
/// for the variables PATH depends on and a short list of well-known folders, and nothing else, because
/// environment variables are a common home for tokens and keys and a snapshot is meant to be shareable.
/// </summary>
public static class EnvironmentCapturePolicy
{
    /// <summary>Values always kept: the folders normalisation and the profile-path checks reason about.</summary>
    public static IReadOnlyList<string> AlwaysCaptured { get; } =
    [
        "Path", "PATHEXT",
        "SystemRoot", "windir", "SystemDrive",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "ProgramData", "ALLUSERSPROFILE", "PUBLIC",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "APPDATA",
    ];

    /// <summary>
    /// The names whose values are kept: <see cref="AlwaysCaptured"/>, everything the PATH values reference, and
    /// everything those variables reference in turn (at any scope).
    /// </summary>
    public static IReadOnlySet<string> RelevantNames(
        IEnumerable<string?> pathValues, IEnumerable<IReadOnlyDictionary<string, string>> sources)
    {
        var sourceList = sources.ToList();
        var relevant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(AlwaysCaptured);

        foreach (var value in pathValues.OfType<string>())
            foreach (var name in EnvironmentExpander.References(value))
                pending.Enqueue(name);

        while (pending.TryDequeue(out var name))
        {
            if (!relevant.Add(name)) continue;
            foreach (var source in sourceList)
                if (Lookup(source, name) is { } value)
                    foreach (var inner in EnvironmentExpander.References(value))
                        pending.Enqueue(inner);
        }

        return relevant;
    }

    /// <summary>Every name from <paramref name="all"/>, with values only for <paramref name="relevant"/> ones.</summary>
    public static EnvironmentVariables Select(
        EnvironmentSource source, IReadOnlyDictionary<string, string> all, IReadOnlySet<string> relevant)
    {
        var kept = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in all)
            kept[name] = relevant.Contains(name) ? value : null;
        return new EnvironmentVariables { Source = source, Variables = new Dictionary<string, string?>(kept, StringComparer.OrdinalIgnoreCase) };
    }

    static string? Lookup(IReadOnlyDictionary<string, string> source, string name)
    {
        if (source.TryGetValue(name, out var value)) return value;
        foreach (var (key, v) in source)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }
}
