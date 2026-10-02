using Pathology.Core.Model;

namespace Pathology.Core.Detection.Detectors;

/// <summary>
/// The shared shape of the hygiene rules: one finding per scope listing every entry with the defect, since
/// the fix ("tidy these") is one edit per value.
/// </summary>
public abstract class HygieneDetector : IDetector
{
    public abstract string Rule { get; }
    protected abstract HygieneDefects Defects { get; }
    protected virtual Severity Severity => Severity.Low;
    protected abstract string Title(PathScope scope, int count);
    protected abstract string Why { get; }
    protected abstract string Fix { get; }

    /// <summary>Whether the scope's affected entries amount to a finding at all (all of them, by default).</summary>
    protected virtual bool Applies(IReadOnlyList<PathEntry> affected, IReadOnlyList<PathEntry> all) => affected.Count > 0;

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        foreach (var scope in new[] { PathScope.Machine, PathScope.User })
        {
            var all = context.Snapshot.EntriesIn(scope).ToList();
            var affected = all.Where(e => (e.Defects & Defects) != 0).ToList();
            if (!Applies(affected, all)) continue;

            yield return new Finding
            {
                Rule = Rule,
                Category = FindingCategory.Hygiene,
                Severity = Severity,
                Subject = Words.Scope(scope),
                RootCause = $"{Rule}:{scope}",
                Title = Title(scope, affected.Count),
                What = $"In {Words.Path(scope)}: {Words.List(affected.Select(e => $"#{e.Index + 1} \"{e.Raw}\""))}.",
                Why = Why,
                Fix = Fix,
                Scope = scope,
                Entries = affected.Select(EntryRef.Of).ToList(),
                Perspectives = [Perspective.CurrentUserUnelevated],
            };
        }
    }

    protected static string Entries(int count) => Words.Count(count, "entry", "entries");
}

/// <summary>HYG-01: quotes inside entries.</summary>
public sealed class StrayQuotes : HygieneDetector
{
    public override string Rule => "HYG-01";
    protected override HygieneDefects Defects => HygieneDefects.Quotes;
    protected override string Title(PathScope scope, int count) => $"{Entries(count)} in {Words.Path(scope)} {(count == 1 ? "contains" : "contain")} quotes";
    protected override string Why =>
        "cmd strips quotes from PATH entries, but Windows' own program and DLL search doesn't: to it, \"C:\\Program Files\\x\" is " +
        "a folder whose name starts with a quote, which doesn't exist. PATH entries never need quotes, even with spaces in them.";
    protected override string Fix => "Remove the quotes.";
}

/// <summary>HYG-02: leading or trailing whitespace.</summary>
public sealed class StrayWhitespace : HygieneDetector
{
    public override string Rule => "HYG-02";
    protected override HygieneDefects Defects => HygieneDefects.LeadingWhitespace | HygieneDefects.TrailingWhitespace;
    protected override string Title(PathScope scope, int count) => $"{Entries(count)} in {Words.Path(scope)} {(count == 1 ? "starts or ends" : "start or end")} with spaces";
    protected override string Why =>
        "Some programs trim the spaces and some don't, so the same entry can work in one tool and not in another, and it " +
        "won't match the same folder written without them.";
    protected override string Fix => "Trim the spaces.";
}

/// <summary>HYG-03: doubled backslashes and forward slashes. (Doubled semicolons are empty entries: see COR-03.)</summary>
public sealed class OddSeparators : HygieneDetector
{
    public override string Rule => "HYG-03";
    protected override HygieneDefects Defects => HygieneDefects.DoubledBackslash | HygieneDefects.ForwardSlash;
    protected override string Title(PathScope scope, int count) => $"{Entries(count)} in {Words.Path(scope)} {(count == 1 ? "uses" : "use")} doubled backslashes or forward slashes";
    protected override string Why =>
        "Windows copes with C:\\Tools\\\\bin and C:/Tools/bin in most places, but tools that compare PATH entries as text " +
        "don't see them as C:\\Tools\\bin, and some installers then add the folder a second time.";
    protected override string Fix => "Use single backslashes.";
}

/// <summary>HYG-04: a mix of entries with and without a trailing backslash. Informational: duplicate detection ignores it.</summary>
public sealed class TrailingBackslashMix : HygieneDetector
{
    public override string Rule => "HYG-04";
    protected override HygieneDefects Defects => HygieneDefects.TrailingBackslash;
    protected override Severity Severity => Severity.Info;

    protected override bool Applies(IReadOnlyList<PathEntry> affected, IReadOnlyList<PathEntry> all) =>
        affected.Count > 0 && all.Any(e => e.Form == PathForm.Absolute && (e.Defects & HygieneDefects.TrailingBackslash) == 0 && e.Expanded.Trim().Length > 3);

    protected override string Title(PathScope scope, int count) => $"Some entries in {Words.Path(scope)} end with a backslash and some don't";
    protected override string Why =>
        "Harmless to Windows, but it makes duplicates harder to spot by eye. PATHology ignores the difference when it looks for duplicates.";
    protected override string Fix => "Pick one style. No trailing backslash is the usual one.";
}
