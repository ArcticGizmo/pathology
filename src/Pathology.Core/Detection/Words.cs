using Pathology.Core.Model;

namespace Pathology.Core.Detection;

/// <summary>How findings phrase entries, scopes and counts, so every detector reads the same way.</summary>
internal static class Words
{
    public static string Path(PathScope scope) => scope == PathScope.Machine ? "the machine PATH" : "your user PATH";

    public static string Scope(PathScope scope) => scope == PathScope.Machine ? "machine" : "user";

    /// <summary>"C:\Tools (machine #3)".</summary>
    public static string Entry(PathEntry entry) => $"{Display(entry)} ({Scope(entry.Scope)} #{entry.Index + 1})";

    public static string Display(PathEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Expanded) ? "(empty)" : entry.Expanded.Trim();

    /// <summary>"1 entry", "3 entries".</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>"a, b and c".</summary>
    public static string List(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }
}
