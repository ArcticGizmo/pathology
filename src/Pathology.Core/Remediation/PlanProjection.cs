using Pathology.Core.Capture;
using Pathology.Core.Detection;
using Pathology.Core.Health;
using Pathology.Core.Model;
using Pathology.Core.Normalisation;
using Pathology.Core.Shadowing;

namespace Pathology.Core.Remediation;

/// <summary>A snapshot of the machine as a change set would leave it, diagnosed and rated.</summary>
public sealed record ProjectedScan(PathSnapshot Snapshot, Diagnosis Diagnosis, HealthReport Health);

/// <summary>How one command's resolution changes.</summary>
/// <param name="Before">What runs now, or null when nothing does.</param>
/// <param name="After">What would run, or null when nothing would.</param>
public sealed record CommandChange(string Command, CommandProvider? Before, CommandProvider? After)
{
    public bool Gone => After is null;
    public bool New => Before is null;

    /// <summary>A Windows command is involved on either side.</summary>
    public bool Builtin => Before?.IsWindows == true || After?.IsWindows == true;
}

/// <summary>What a change set does, before and after: the ratings, the problems, and which commands run.</summary>
public sealed record PlanOutcome(
    Diagnosis Before,
    HealthReport BeforeHealth,
    ProjectedScan After,
    IReadOnlyList<FindingGroup> Resolved,
    IReadOnlyList<FindingGroup> Introduced,
    IReadOnlyList<CommandChange> Commands);

/// <summary>
/// Works out what the machine would look like after a change set, by running the real
/// <see cref="SnapshotCapturer"/> again over readers that replay the snapshot: the proposed PATH values, the
/// captured environment, the captured folders and the designed permissions. So the "after" is what a scan of the
/// fixed machine would say, rule for rule.
/// </summary>
/// <remarks>
/// Pure unless the caller supplies <paramref name="probe"/> and <paramref name="evaluator"/>: they're consulted only
/// for what the scan never captured (a folder added by hand, a designed SDDL), and both are read-only.
/// </remarks>
public static class PlanProjection
{
    public static PlanOutcome Project(
        Diagnosis before, ChangeSet changes, IAccessEvaluator? evaluator = null, IDirectoryProbe? probe = null)
    {
        var snapshot = before.Snapshot;
        var after = Capture(snapshot, changes, evaluator, probe);
        var diagnosis = Diagnoser.Diagnose(after);
        var projected = new ProjectedScan(after, diagnosis, HealthRater.Rate(diagnosis));

        var beforeKeys = before.Findings.Select(Signature).ToHashSet(StringComparer.Ordinal);
        var afterKeys = diagnosis.Findings.Select(Signature).ToHashSet(StringComparer.Ordinal);
        var resolved = before.Groups.Where(g => g.Members.All(f => !afterKeys.Contains(Signature(f)))).ToList();
        var introduced = diagnosis.Groups.Where(g => g.Members.All(f => !beforeKeys.Contains(Signature(f)))).ToList();

        return new PlanOutcome(before, HealthRater.Rate(before), projected, resolved, introduced,
            CompareCommands(before.Shadows, diagnosis.Shadows));
    }

    /// <summary>
    /// What identifies a problem across the two snapshots: the rule and its root cause, which name folders, values
    /// and scopes. The one exception is a cause named by position (<c>entry:Machine:4</c>), since positions shift
    /// when entries are removed: there it's the entry's text.
    /// </summary>
    static string Signature(Finding f) =>
        f.Rule + "|" + (f.RootCause.StartsWith("entry:", StringComparison.Ordinal)
            ? string.Join(";", f.Entries.Select(e => $"{e.Scope}:{PathText.Key(PathText.Strip(e.Display))}"))
            : f.RootCause);

    /// <summary>Every command whose winning file changes, appears or disappears: Windows commands first.</summary>
    public static IReadOnlyList<CommandChange> CompareCommands(ShadowReport before, ShadowReport after)
    {
        var names = before.All.Select(r => r.Command).Concat(after.All.Select(r => r.Command))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var changes = new List<CommandChange>();
        foreach (var name in names)
        {
            var was = before.Resolve(name)?.Winner;
            var will = after.Resolve(name)?.Winner;
            if (was is not null && will is not null && PathText.Key(was.FullPath) == PathText.Key(will.FullPath)) continue;
            changes.Add(new CommandChange(was?.Command ?? will!.Command, was, will));
        }
        return changes
            .OrderBy(c => c.New ? 2 : c.Gone ? 1 : 0)
            .ThenByDescending(c => c.Builtin)
            .ThenBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The snapshot as the change set would leave it.</summary>
    public static PathSnapshot Capture(
        PathSnapshot snapshot, ChangeSet changes, IAccessEvaluator? evaluator = null, IDirectoryProbe? probe = null)
    {
        var acls = changes.Acls
            .Where(a => a.Refusal is null && a.AfterSddl is not null)
            .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var captured = new SnapshotCapturer(
                new ReplayRegistry(snapshot, changes),
                new ReplayEnvironment(snapshot),
                new ReplayHost(snapshot.Host),
                new ReplayPerspectives(snapshot.Perspectives),
                new ReplayProbe(snapshot, acls, probe),
                new ReplayEvaluator(snapshot, evaluator),
                new FixedClock(snapshot.CapturedAt))
            .Capture(snapshot.Options);

        // The environment doesn't change with PATH, so it's the scan's, with each PATH value updated. Explorer is
        // told about the change when it's applied, so the process PATH is the new one too.
        var effective = string.Join(';', captured.Entries.Select(e => e.Expanded));
        return captured with
        {
            Redacted = snapshot.Redacted,
            Environment = snapshot.Environment.Select(set => set.Source switch
            {
                EnvironmentSource.Machine => WithPath(set, changes.Resulting(snapshot, PathScope.Machine).Value),
                EnvironmentSource.User => WithPath(set, changes.Resulting(snapshot, PathScope.User).Value),
                EnvironmentSource.NewProcess or EnvironmentSource.CurrentProcess => WithPath(set, effective),
                _ => set,
            }).ToList(),
            EffectivePath = effective,
            ProcessPath = effective,
        };
    }

    static EnvironmentVariables WithPath(EnvironmentVariables set, string? value)
    {
        var name = set.Variables.Keys.FirstOrDefault(k => string.Equals(k, "Path", StringComparison.OrdinalIgnoreCase));
        if (name is null && value is null) return set;
        var variables = new Dictionary<string, string?>(set.Variables, StringComparer.OrdinalIgnoreCase);
        if (value is null) variables.Remove(name!);
        else variables[name ?? "Path"] = value;
        return set with { Variables = variables };
    }

    static Dictionary<string, string> Values(PathSnapshot snapshot, EnvironmentSource source)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in snapshot.EnvironmentFrom(source)?.Variables ?? new Dictionary<string, string?>())
            if (value is not null) values[name] = value;
        return values;
    }

    sealed class ReplayRegistry(PathSnapshot snapshot, ChangeSet changes) : IRegistryPathReader
    {
        public RegistryEnvironment Read() => new(
            changes.Resulting(snapshot, PathScope.Machine),
            changes.Resulting(snapshot, PathScope.User),
            Values(snapshot, EnvironmentSource.Machine),
            Values(snapshot, EnvironmentSource.User),
            Values(snapshot, EnvironmentSource.Volatile));
    }

    sealed class ReplayEnvironment(PathSnapshot snapshot) : IEffectiveEnvironmentReader
    {
        public IReadOnlyDictionary<string, string> ReadNewProcessEnvironment() => Values(snapshot, EnvironmentSource.NewProcess);
        public IReadOnlyDictionary<string, string> ReadCurrentProcessEnvironment() => Values(snapshot, EnvironmentSource.CurrentProcess);
    }

    sealed class ReplayHost(HostInfo host) : IHostInfoReader
    {
        public HostInfo Read() => host;
    }

    sealed class ReplayPerspectives(IReadOnlyList<PerspectiveIdentity> identities) : ITokenPerspectives
    {
        public IReadOnlyList<PerspectiveIdentity> Build() => identities;
    }

    /// <summary>The captured folders, with designed permissions in place. Anything else goes to the live probe, if any.</summary>
    sealed class ReplayProbe(PathSnapshot snapshot, Dictionary<string, AclChange> acls, IDirectoryProbe? live) : IDirectoryProbe
    {
        public DirectoryFacts Probe(string path, bool allowNetwork)
        {
            if (snapshot.FactsFor(path) is not { } facts)
                return live?.Probe(path, allowNetwork)
                       ?? new DirectoryFacts { Path = path, Status = ProbeStatus.Failed, Note = "Not looked at: the scan didn't include this folder." };

            facts = facts with { Access = [], CommandFiles = null };
            return acls.TryGetValue(path, out var acl)
                ? facts with { Sddl = acl.AfterSddl, OwnerSid = acl.AfterOwnerSid ?? facts.OwnerSid }
                : facts;
        }

        public IReadOnlyList<string>? ListFiles(string path, IReadOnlySet<string> extensions, bool allowNetwork) =>
            snapshot.FactsFor(path)?.CommandFiles ?? live?.ListFiles(path, extensions, allowNetwork);
    }

    /// <summary>The captured access results by SDDL; a designed SDDL goes to the live evaluator, if any.</summary>
    sealed class ReplayEvaluator(PathSnapshot snapshot, IAccessEvaluator? live) : IAccessEvaluator
    {
        readonly Dictionary<(string, Perspective), AccessResult> _known = snapshot.Directories
            .Where(d => d.Sddl is not null)
            .SelectMany(d => d.Access.Select(a => (Key: (d.Sddl!, a.Perspective), Access: a)))
            .DistinctBy(p => p.Key)
            .ToDictionary(p => p.Key, p => p.Access);

        public AccessResult Evaluate(string sddl, PerspectiveIdentity identity) =>
            _known.TryGetValue((sddl, identity.Perspective), out var access) ? access
            : live?.Evaluate(sddl, identity)
              ?? new AccessResult { Perspective = identity.Perspective, Error = "Not evaluated: no access evaluator was given." };
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
