using Pathology.Core.Model;

namespace Pathology.Core.Detection;

/// <summary>How bad a finding is. Ordered, so <c>a &gt; b</c> means "a is worse".</summary>
public enum Severity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>Which sub-score a finding counts against.</summary>
public enum FindingCategory
{
    Security,
    Correctness,
    Hygiene,
}

/// <summary>A pointer to one PATH entry, with enough text to show it without the snapshot.</summary>
public sealed record EntryRef(PathScope Scope, int Index, string Raw, string Expanded)
{
    public static EntryRef Of(PathEntry entry) => new(entry.Scope, entry.Index, entry.Raw, entry.Expanded);

    /// <summary>What to show: the expanded text when it differs usefully, else the raw text.</summary>
    public string Display => string.IsNullOrWhiteSpace(Expanded) ? Raw : Expanded.Trim();

    public override string ToString() => $"{(Scope == PathScope.Machine ? "machine" : "user")} #{Index + 1}: {Display}";
}

/// <summary>
/// One problem a detector found. The text is advisory prose in v1.0: <see cref="What"/> says what's wrong
/// here, <see cref="Why"/> why it matters, and <see cref="Fix"/> what to do about it.
/// </summary>
public sealed record Finding
{
    /// <summary>The rule that raised it, e.g. <c>SEC-01</c>.</summary>
    public required string Rule { get; init; }

    public required FindingCategory Category { get; init; }

    public required Severity Severity { get; init; }

    /// <summary>
    /// What the finding is about (a directory, a value, a command), so <see cref="Key"/> stays the same from
    /// one scan to the next.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Findings with the same root cause are one problem seen from several rules (a missing folder is both
    /// dead and a phantom-directory risk; every folder under a permissive drive root shares that root). They're
    /// grouped under the worst of them and cost once.
    /// </summary>
    public required string RootCause { get; init; }

    /// <summary>One line, for a card or a list row.</summary>
    public required string Title { get; init; }

    public required string What { get; init; }
    public required string Why { get; init; }
    public required string Fix { get; init; }

    /// <summary>The scope the finding concerns, or null when it spans both.</summary>
    public PathScope? Scope { get; init; }

    /// <summary>The entries involved, in search order.</summary>
    public IReadOnlyList<EntryRef> Entries { get; init; } = [];

    /// <summary>The perspectives involved: who could exploit it, and who it would be used against.</summary>
    public IReadOnlyList<Perspective> Perspectives { get; init; } = [];

    /// <summary>Supporting facts, one per line: the ACE that grants access, the contexts that break, a diff.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>The Learn article that explains the concept (M4 deep-links to it).</summary>
    public string? Learn { get; init; }

    /// <summary>Stable across scans: the rule plus its subject.</summary>
    public string Key => $"{Rule}|{Subject}";
}

/// <summary>Names of the Learn articles findings link to (M4 writes them).</summary>
public static class LearnTopics
{
    public const string DllSearchOrder = "dll-search-order";
    public const string PathExt = "pathext";
    public const string UacAndPath = "uac-and-path";
    public const string NewProcessPath = "new-process-path";
    public const string ValueKinds = "reg-sz-vs-reg-expand-sz";
    public const string PhantomDirectories = "phantom-directories";
    public const string WhyNotSetx = "why-not-setx";
}
