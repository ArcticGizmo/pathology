using Pathology.Core.Detection.Detectors;
using Pathology.Core.Model;
using Pathology.Core.Shadowing;

namespace Pathology.Core.Detection;

/// <summary>One symptom rule. Pure: the same context always gives the same findings, and nothing is touched.</summary>
public interface IDetector
{
    /// <summary>The rule ID every finding it raises carries, e.g. <c>SEC-01</c>.</summary>
    string Rule { get; }

    IEnumerable<Finding> Detect(DetectionContext context);
}

/// <summary>Findings that share a root cause: one problem, shown and counted once, under its worst finding.</summary>
public sealed record FindingGroup(string RootCause, Finding Primary, IReadOnlyList<Finding> Members)
{
    public Severity Severity => Primary.Severity;

    /// <summary>The members other than <see cref="Primary"/>.</summary>
    public IEnumerable<Finding> Related => Members.Where(f => !ReferenceEquals(f, Primary));
}

/// <summary>The outcome of diagnosing one snapshot.</summary>
public sealed record Diagnosis(PathSnapshot Snapshot, IReadOnlyList<Finding> Findings, IReadOnlyList<FindingGroup> Groups, ShadowReport Shadows)
{
    public int Count(Severity severity) => Findings.Count(f => f.Severity == severity);
}

/// <summary>Runs every detector over a snapshot, ranks the findings and groups them by root cause.</summary>
public static class Diagnoser
{
    /// <summary>
    /// Every rule, in the order ties are broken: where two findings share a root cause and a severity, the
    /// earlier rule leads the group. Umbrella rules (the inherited-ACL root cause) come first for that reason.
    /// </summary>
    public static IReadOnlyList<IDetector> All { get; } =
    [
        new InheritedPermissiveAcl(),        // SEC-05
        new MachineFolderWritable(),         // SEC-01
        new FolderOwnedByNonAdmin(),         // SEC-02
        new PhantomMachineFolder(),          // SEC-03
        new WritableBeforeSystem32(),        // SEC-04
        new UserFolderWritableByOthers(),    // SEC-06
        new UacExposure(),                   // SEC-07
        new WritableLinkTarget(),            // SEC-08
        new FragileLocation(),               // SEC-09
        new UserOnlyVariableInMachinePath(), // COR-01
        new VariableInRegSz(),               // COR-02
        new RelativeOrEmptyEntry(),          // COR-03
        new ProfilePathInMachinePath(),      // COR-04
        new DeadEntry(),                     // COR-05
        new DuplicateEntry(),                // COR-06
        new CompetingExecutables(),          // COR-07
        new LengthHeadroom(),                // COR-08
        new WindowsFoldersMissing(),         // COR-09
        new StrayQuotes(),                   // HYG-01
        new StrayWhitespace(),               // HYG-02
        new OddSeparators(),                 // HYG-03
        new TrailingBackslashMix(),          // HYG-04
        new EffectiveDiffersFromRegistry(),  // CFG-01
        new StaleProcessPath(),              // CFG-02
    ];

    public static Diagnosis Diagnose(PathSnapshot snapshot, IEnumerable<IDetector>? detectors = null)
    {
        var context = new DetectionContext(snapshot);
        var rules = (detectors ?? All).ToList();
        var order = rules.Select((d, i) => (d.Rule, i)).ToDictionary(p => p.Rule, p => p.i);

        var findings = rules.SelectMany(d => d.Detect(context))
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ThenBy(f => order.GetValueOrDefault(f.Rule, int.MaxValue))
            .ThenBy(f => FirstPosition(f, snapshot))
            .ThenBy(f => f.Subject, StringComparer.Ordinal)
            .ToList();

        // Findings are already ranked, so each group's first member is its worst.
        var groups = findings
            .GroupBy(f => f.RootCause, StringComparer.Ordinal)
            .Select(g => new FindingGroup(g.Key, g.First(), g.ToList()))
            .ToList();

        return new Diagnosis(snapshot, findings, groups, context.Shadows);
    }

    static int FirstPosition(Finding finding, PathSnapshot snapshot)
    {
        if (finding.Entries.Count == 0) return int.MaxValue;
        var first = finding.Entries[0];
        var machineCount = snapshot.EntriesIn(PathScope.Machine).Count();
        return first.Scope == PathScope.Machine ? first.Index : machineCount + first.Index;
    }
}
