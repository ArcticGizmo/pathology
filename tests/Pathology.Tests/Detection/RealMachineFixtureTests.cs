using Pathology.Core.Detection;
using Pathology.Core.Health;
using Pathology.Core.Model;
using Pathology.Core.Redaction;

namespace Pathology.Tests.Detection;

/// <summary>
/// The whole engine over a real developer PC, captured with <c>pathology snapshot</c> (redacted, names
/// pseudonymised) on 2026-10-01. It's the M2/M3 verification machine, so these are the answers every finding
/// on it was traced by hand to a real cause. Re-capture it rather than editing it: <c>pathology snapshot
/// tests/Pathology.Tests/Fixtures/dev-machine.redacted.json</c>, then review it before committing.
/// </summary>
public class RealMachineFixtureTests
{
    static readonly PathSnapshot Snapshot = PathSnapshotJson.Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dev-machine.redacted.json"))
                                            ?? throw new InvalidOperationException("fixture missing");

    static readonly Diagnosis Diagnosis = Diagnoser.Diagnose(Snapshot);
    static readonly HealthReport Health = HealthRater.Rate(Diagnosis);

    [Fact]
    public void The_fixture_is_redacted_and_stays_so()
    {
        Assert.True(Snapshot.Redacted);
        Assert.Equal(SnapshotRedactor.UserPlaceholder, Snapshot.Host.UserName);
        Assert.Equal(SnapshotRedactor.MachinePlaceholder, Snapshot.Host.MachineName);
        // Only placeholder account SIDs (S-1-5-21-0-0-n, S-1-12-1-0-0-0-n) are left: no real domain or tenant.
        var json = PathSnapshotJson.Serialize(Snapshot);
        Assert.DoesNotMatch(@"S-1-5-21-(?!0-0-)\d", json);
        Assert.DoesNotMatch(@"S-1-12-1-(?!0-0-0-)\d", json);
        Assert.Equal(json, PathSnapshotJson.Serialize(SnapshotRedactor.Redact(Snapshot)));
        Assert.False(Snapshot.Options.ProbeNetworkPaths);
    }

    [Fact]
    public void The_ratings_are_the_ones_verified_by_hand()
    {
        var security = Health[FindingCategory.Security];
        Assert.Equal(Severity.High, security.Rating);
        // The second Medium was the UAC exposure summary, which is a note now.
        Assert.Equal((9, 1, 0), (security.Count(Severity.High), security.Count(Severity.Medium), security.Count(Severity.Low)));

        var correctness = Health[FindingCategory.Correctness];
        Assert.Equal(Severity.High, correctness.Rating);
        Assert.Equal((2, 1, 3), (correctness.Count(Severity.High), correctness.Count(Severity.Medium), correctness.Count(Severity.Low)));

        Assert.Equal(Severity.Low, Health[FindingCategory.Hygiene].Rating);
        Assert.Equal(13, Health.Notes.Count);
    }

    [Fact]
    public void One_drive_root_acl_is_one_problem_however_many_folders_it_reaches()
    {
        var group = Assert.Single(Diagnosis.Groups, g => g.RootCause == @"inherited:C:\");
        Assert.Equal("SEC-05", group.Primary.Rule);
        Assert.Equal(7, group.Primary.Entries.Count);
        Assert.Contains(group.Members, f => f.Rule == "SEC-01");
        Assert.Contains(group.Members, f => f.Rule == "SEC-04");
    }

    [Fact]
    public void Missing_folders_anyone_could_create_are_phantoms_and_lead_their_dead_entry()
    {
        var phantoms = Diagnosis.Groups.Where(g => g.Primary.Rule == "SEC-03").ToList();
        Assert.Equal(3, phantoms.Count);
        Assert.All(phantoms, g => Assert.Contains(g.Members, f => f.Rule == "COR-05" && f.Severity == Severity.Low));
    }

    [Fact]
    public void A_user_only_variable_in_the_machine_path_and_the_length_limit_are_the_correctness_highs()
    {
        var highs = Health[FindingCategory.Correctness].Holding.Select(g => g.Primary.Rule).Order().ToList();
        Assert.Equal(["COR-01", "COR-08"], highs);
        Assert.Contains("%NVM_SYMLINK%", Diagnosis.Findings.Single(f => f.Rule == "COR-01").Title);
    }

    [Fact]
    public void Shadowing_still_resolves_over_pseudonymised_names()
    {
        // Windows' own names stay readable, so a lookup of a built-in works and lands in System32.
        var where = Diagnosis.Shadows.Resolve("where");
        Assert.NotNull(where);
        Assert.True(where.Winner.IsWindows);

        // Two JDKs side by side: one finding for the pair, not one per command.
        Assert.Single(Diagnosis.Findings, f => f.Rule == "COR-07" && f.Title.Contains("hides 36 commands"));
    }
}
