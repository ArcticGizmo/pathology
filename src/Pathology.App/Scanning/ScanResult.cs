using Pathology.Core.Detection;
using Pathology.Core.Health;
using Pathology.Core.Model;

namespace Pathology.App.Scanning;

/// <summary>
/// One scan, evaluated: the snapshot, what the detectors made of it, and the category ratings. Every page reads
/// the same instance, so they can never disagree about what the machine looks like.
/// </summary>
public sealed class ScanResult
{
    ScanResult(PathSnapshot snapshot)
    {
        Snapshot = snapshot;
        Diagnosis = Diagnoser.Diagnose(snapshot);
        Health = HealthRater.Rate(Diagnosis);
        Context = new DetectionContext(snapshot);
    }

    /// <summary>Diagnose and rate a snapshot. Pure: nothing on the machine is touched.</summary>
    public static ScanResult Of(PathSnapshot snapshot) => new(snapshot);

    public PathSnapshot Snapshot { get; }
    public Diagnosis Diagnosis { get; }
    public HealthReport Health { get; }

    /// <summary>The snapshot's entries resolved through links, plus who counts as an admin (for the Entries page).</summary>
    public DetectionContext Context { get; }

    /// <summary>The root-cause group a finding belongs to.</summary>
    public FindingGroup? GroupOf(string rootCause) =>
        Diagnosis.Groups.FirstOrDefault(g => string.Equals(g.RootCause, rootCause, StringComparison.Ordinal));

    /// <summary>The groups with a finding about this entry, worst first.</summary>
    public IEnumerable<FindingGroup> GroupsFor(PathEntry entry) =>
        Diagnosis.Groups.Where(g => g.Members.Any(f => f.Entries.Any(e => e.Scope == entry.Scope && e.Index == entry.Index)));
}
