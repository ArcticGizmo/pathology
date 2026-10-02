namespace Pathology.Core.Model;

/// <summary>What an entry's text looks like, decided from the string alone (nothing is touched to classify it).</summary>
public enum PathForm
{
    /// <summary>Empty or whitespace only.</summary>
    Empty,

    /// <summary>Fully qualified with a drive letter: <c>C:\Tools</c> (also <c>\\?\C:\Tools</c>).</summary>
    Absolute,

    /// <summary><c>C:Tools</c>: relative to that drive's current directory.</summary>
    DriveRelative,

    /// <summary><c>\Tools</c>: relative to the current drive's root.</summary>
    RootRelative,

    /// <summary><c>Tools</c>, <c>.</c>, <c>..\bin</c>: relative to the current directory.</summary>
    Relative,

    /// <summary><c>\\server\share\…</c> (also <c>\\?\UNC\…</c>). Never probed unless the user opts in.</summary>
    Unc,

    /// <summary>A device path such as <c>\\?\Volume{…}\</c>.</summary>
    DevicePath,
}

/// <summary>Text-level defects in one entry. Found by the tokeniser, before any expansion.</summary>
[Flags]
public enum HygieneDefects
{
    None = 0,

    /// <summary>Empty or whitespace only (from <c>;;</c>, or a leading <c>;</c>).</summary>
    Empty = 1 << 0,

    /// <summary>The empty entry a trailing <c>;</c> leaves at the end of the value.</summary>
    TrailingSeparator = 1 << 1,

    LeadingWhitespace = 1 << 2,
    TrailingWhitespace = 1 << 3,

    /// <summary>Contains <c>"</c>. cmd strips quotes; the loader and <c>CreateProcess</c> search do not.</summary>
    Quotes = 1 << 4,

    /// <summary><c>\\</c> anywhere other than a UNC or device prefix.</summary>
    DoubledBackslash = 1 << 5,

    /// <summary>Ends in <c>\</c> (other than a drive root). Harmless alone; matters for duplicate detection.</summary>
    TrailingBackslash = 1 << 6,

    /// <summary>Uses <c>/</c> as a separator.</summary>
    ForwardSlash = 1 << 7,
}

/// <summary>One <c>;</c>-separated entry of a stored PATH value, in search order.</summary>
public sealed record PathEntry
{
    public PathScope Scope { get; init; }

    /// <summary>Zero-based position within its scope's value (empty entries keep their slot).</summary>
    public int Index { get; init; }

    /// <summary>The entry exactly as stored, including any whitespace and quotes.</summary>
    public string Raw { get; init; } = "";

    /// <summary>
    /// The entry as Windows puts it into a new process's PATH. A <c>REG_SZ</c> value isn't expanded, so for
    /// those this equals <see cref="Raw"/>.
    /// </summary>
    public string Expanded { get; init; } = "";

    public PathForm Form { get; init; }

    public HygieneDefects Defects { get; init; }

    /// <summary>Every <c>%VAR%</c> the raw text references, in order of first use.</summary>
    public IReadOnlyList<string> Variables { get; init; } = [];

    /// <summary>References that stayed literal: undefined where this scope is expanded, or inside a <c>REG_SZ</c> value.</summary>
    public IReadOnlyList<string> UnresolvedVariables { get; init; } = [];

    /// <summary>
    /// The cleaned, fully qualified directory that was probed, and the key into
    /// <see cref="PathSnapshot.Directories"/>. Null when the entry can't be probed without guessing a current
    /// directory (relative, drive-relative, root-relative or empty entries).
    /// </summary>
    public string? ProbePath { get; init; }

    /// <summary>
    /// The duplicate-detection key: expanded, unquoted, trimmed, separators and dots collapsed, 8.3 names
    /// expanded (when the folder exists), no trailing backslash, upper-cased.
    /// </summary>
    public string Key { get; init; } = "";
}
