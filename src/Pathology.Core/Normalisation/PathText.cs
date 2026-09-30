using Pathology.Core.Model;

namespace Pathology.Core.Normalisation;

/// <summary>
/// String-only path handling: classify, clean, key, walk up. Nothing here touches the file system, so it
/// behaves the same on any OS and for any path, including a UNC path that must not be opened.
/// </summary>
public static class PathText
{
    /// <summary>Whitespace trimmed, quotes removed, <c>/</c> turned into <c>\</c>: the text Windows would actually try.</summary>
    public static string Strip(string text) => text.Trim().Replace("\"", "").Replace('/', '\\').Trim();

    /// <summary>Classify an entry from its text. Pass it through <see cref="Strip"/> first.</summary>
    public static PathForm Classify(string stripped)
    {
        var s = stripped;
        if (s.Length == 0) return PathForm.Empty;
        if (StartsWith(s, @"\\?\UNC\") || StartsWith(s, @"\\.\UNC\")) return PathForm.Unc;
        if (StartsWith(s, @"\\?\") || StartsWith(s, @"\\.\"))
            return IsDriveRooted(s.AsSpan(4)) ? PathForm.Absolute : PathForm.DevicePath;
        if (s.StartsWith(@"\\", StringComparison.Ordinal)) return PathForm.Unc;
        if (s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':')
            return s.Length >= 3 && s[2] == '\\' ? PathForm.Absolute : PathForm.DriveRelative;
        if (s[0] == '\\') return PathForm.RootRelative;
        return PathForm.Relative;
    }

    /// <summary>
    /// The canonical fully qualified form of a stripped path, or null when it can't be resolved without a current
    /// directory. Collapses doubled separators, resolves <c>.</c> and <c>..</c>, drops the trailing dots and
    /// spaces Win32 ignores, removes a trailing backslash, upper-cases the drive letter, and turns
    /// <c>\\?\C:\x</c> into <c>C:\x</c> and <c>\\?\UNC\h\s</c> into <c>\\h\s</c>.
    /// </summary>
    public static string? Canonical(string stripped)
    {
        var form = Classify(stripped);
        var s = stripped;
        switch (form)
        {
            case PathForm.Absolute:
                if (StartsWith(s, @"\\?\") || StartsWith(s, @"\\.\")) s = s[4..];
                return Join(char.ToUpperInvariant(s[0]) + @":\", s[3..], minKept: 0);

            case PathForm.Unc:
                if (StartsWith(s, @"\\?\UNC\") || StartsWith(s, @"\\.\UNC\")) s = s[8..];
                else s = s.TrimStart('\\');
                // \\server\share is the root: .. never climbs above it.
                return Join(@"\\", s, minKept: 2);

            case PathForm.DevicePath:
                // \\?\Volume{guid}\rest: the volume name is the root.
                return Join(s[..4], s[4..], minKept: 1);

            default:
                return null;
        }
    }

    /// <summary>The duplicate-detection key for a canonical path (or, failing that, the stripped text).</summary>
    public static string Key(string canonicalOrStripped) => TrimTrailingSeparator(canonicalOrStripped).ToUpperInvariant();

    /// <summary>The parent of a canonical path, or null at its root (<c>C:\</c>, <c>\\server\share</c>).</summary>
    public static string? Parent(string canonical)
    {
        var root = RootOf(canonical);
        if (root is null || canonical.TrimEnd('\\').Length <= root.TrimEnd('\\').Length) return null;
        var parent = canonical[..canonical.LastIndexOf('\\')];
        if (parent.Length >= root.Length) return parent;
        // Back at the root. A drive root keeps its backslash (C:\); a share or volume root doesn't.
        return Classify(canonical) == PathForm.Absolute ? root : root.TrimEnd('\\');
    }

    /// <summary>
    /// The root of a canonical path: <c>C:\</c>, <c>\\server\share\</c> or <c>\\?\Volume{…}\</c>, or null.
    /// </summary>
    public static string? RootOf(string canonical)
    {
        switch (Classify(canonical))
        {
            case PathForm.Absolute:
                return char.ToUpperInvariant(canonical[0]) + @":\";
            case PathForm.Unc:
            {
                var parts = canonical[2..].Split('\\');
                return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}\" : $@"\\{parts[0]}\";
            }
            case PathForm.DevicePath:
            {
                var end = canonical.IndexOf('\\', 4);
                return end < 0 ? canonical + @"\" : canonical[..(end + 1)];
            }
            default:
                return null;
        }
    }

    /// <summary>True when the string alone says this is on another machine.</summary>
    public static bool IsUnc(string stripped) => Classify(stripped) == PathForm.Unc;

    static string Join(string root, string rest, int minKept)
    {
        var kept = new List<string>();
        foreach (var raw in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".") continue;
            if (raw == "..")
            {
                if (kept.Count > minKept) kept.RemoveAt(kept.Count - 1);
                continue;
            }
            // Win32 drops trailing dots and spaces from each component: C:\Tools. is C:\Tools.
            var segment = raw.TrimEnd('.', ' ');
            if (segment.Length > 0) kept.Add(segment);
        }
        return root + string.Join('\\', kept);
    }

    static string TrimTrailingSeparator(string path)
    {
        var trimmed = path.TrimEnd('\\');
        // Keep the backslash of a drive root, so C:\ and C: (a drive-relative entry) stay distinct.
        if (trimmed.Length == 2 && trimmed[1] == ':' && path.Length > 2) return trimmed + @"\";
        return trimmed.Length == 0 ? path : trimmed;
    }

    static bool IsDriveRooted(ReadOnlySpan<char> s) => s.Length >= 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && s[2] == '\\';

    static bool StartsWith(string s, string prefix) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
