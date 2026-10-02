using Pathology.Core.Model;

namespace Pathology.Core.Normalisation;

/// <summary>One <c>;</c>-separated piece of a stored PATH value, before expansion.</summary>
public readonly record struct PathSegment(int Index, string Text, HygieneDefects Defects);

/// <summary>
/// Splits a stored PATH value into entries and records each one's text-level defects. Splitting is on every
/// <c>;</c>, as the loader and <c>CreateProcess</c> do; only cmd treats a quoted <c>;</c> specially, and a quote
/// in PATH is flagged either way.
/// </summary>
public static class PathTokeniser
{
    public static IReadOnlyList<PathSegment> Split(string? value)
    {
        if (string.IsNullOrEmpty(value)) return [];

        var parts = value.Split(';');
        var segments = new PathSegment[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var defects = Inspect(parts[i]);
            if ((defects & HygieneDefects.Empty) != 0 && i == parts.Length - 1 && i > 0)
                defects |= HygieneDefects.TrailingSeparator;
            segments[i] = new PathSegment(i, parts[i], defects);
        }
        return segments;
    }

    /// <summary>The hygiene defects in one entry's text.</summary>
    public static HygieneDefects Inspect(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return HygieneDefects.Empty;

        var defects = HygieneDefects.None;
        if (char.IsWhiteSpace(text[0])) defects |= HygieneDefects.LeadingWhitespace;
        if (char.IsWhiteSpace(text[^1])) defects |= HygieneDefects.TrailingWhitespace;
        if (text.Contains('"')) defects |= HygieneDefects.Quotes;
        if (text.Contains('/')) defects |= HygieneDefects.ForwardSlash;

        var path = PathText.Strip(text);
        // A leading \\ is a UNC or device prefix, so look for doubles from the second character on.
        if (path.Length > 1 && path.IndexOf(@"\\", 1, StringComparison.Ordinal) >= 0)
            defects |= HygieneDefects.DoubledBackslash;
        if (path.EndsWith('\\') && !IsDriveRoot(path))
            defects |= HygieneDefects.TrailingBackslash;

        return defects;
    }

    static bool IsDriveRoot(string path) => path.Length == 3 && path[1] == ':';
}
