using Pathology.Core.Model;

namespace Pathology.Core.Detection.Detectors;

/// <summary>
/// The entries of a PATH string, trimmed, without empties or repeats: what two PATHs are compared by. Repeats
/// go because Windows drops them itself when it builds a new process's PATH (a user entry that repeats a
/// machine one never reaches it), so they aren't a difference worth reporting.
/// </summary>
internal static class PathList
{
    public static List<string> Of(string? path) =>
        (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool SameSequence(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

    /// <summary>"+ x" for entries only in <paramref name="actual"/>, "- y" for entries only in <paramref name="expected"/>.</summary>
    public static List<string> Diff(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string actualName, string expectedName)
    {
        var lines = actual.Except(expected, StringComparer.OrdinalIgnoreCase).Select(e => $"+ {e} (only in {actualName})").ToList();
        lines.AddRange(expected.Except(actual, StringComparer.OrdinalIgnoreCase).Select(e => $"- {e} (only in {expectedName})"));
        if (lines.Count == 0) lines.Add("The same entries, in a different order");
        return lines;
    }
}

/// <summary>
/// CFG-01: the PATH a new process gets differs from the registry values' expansion (machine, then user).
/// Something between the two is editing it: a policy, a logon script, or an expansion rule not modelled here.
/// </summary>
public sealed class EffectiveDiffersFromRegistry : IDetector
{
    public string Rule => "CFG-01";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        if (context.Snapshot.EffectivePath is not { } effectivePath) yield break;

        // Rejoin and split again: one expanded variable can hold several entries.
        var expected = PathList.Of(string.Join(';', context.Entries.Select(e => e.Expanded)));
        var effective = PathList.Of(effectivePath);
        if (PathList.SameSequence(expected, effective)) yield break;

        yield return new Finding
        {
            Rule = Rule,
            Category = FindingCategory.Correctness,
            Severity = Severity.Medium,
            Subject = "effective",
            RootCause = "cfg:effective",
            Title = "A new process's PATH isn't what the registry values add up to",
            What = "Windows builds each new process's PATH from the machine value followed by the user value. The PATH a new " +
                   "process actually gets here is different.",
            Why = "Something between the registry and new processes is changing PATH, so editing the registry values may not " +
                  "do what you expect, and what PATHology reports about them may not be the whole story.",
            Fix = "Look for Group Policy environment preferences (Computer or User Configuration → Preferences → Windows " +
                  "Settings → Environment) and logon scripts that set PATH.",
            Perspectives = [Perspective.CurrentUserUnelevated],
            Evidence = PathList.Diff(expected, effective, "the new-process PATH", "the registry values"),
            Learn = LearnTopics.NewProcessPath,
        };
    }
}

/// <summary>CFG-02: this process's own PATH differs from a new process's: whatever started it had an old or altered PATH.</summary>
public sealed class StaleProcessPath : IDetector
{
    public string Rule => "CFG-02";

    public IEnumerable<Finding> Detect(DetectionContext context)
    {
        if (context.Snapshot.EffectivePath is not { } effectivePath || context.Snapshot.ProcessPath is not { } processPath) yield break;

        var effective = PathList.Of(effectivePath);
        var process = PathList.Of(processPath);
        if (PathList.SameSequence(effective, process)) yield break;

        yield return new Finding
        {
            Rule = Rule,
            Category = FindingCategory.Correctness,
            Severity = Severity.Info,
            Subject = "process",
            RootCause = "cfg:process",
            Title = "PATHology was started with a different PATH from the one new programs get",
            What = "Programs inherit PATH from whatever starts them. This one's PATH doesn't match what a newly started process would get.",
            Why = "Explorer reads PATH when you sign in and updates only when a program announces a change, so a change made " +
                  "without that announcement reaches new windows only after you sign out. A terminal or IDE may also add entries of its own.",
            Fix = "Sign out and back in, or restart Explorer, to pick up the current PATH. If PATHology was started from a " +
                  "terminal or IDE, the difference may just be that tool's own additions.",
            Perspectives = [Perspective.CurrentUserUnelevated],
            Evidence = PathList.Diff(effective, process, "this process's PATH", "a new process's PATH"),
            Learn = LearnTopics.NewProcessPath,
        };
    }
}
