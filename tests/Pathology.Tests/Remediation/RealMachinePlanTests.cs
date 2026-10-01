using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Tests.Remediation;

/// <summary>
/// The planner over the real developer PC fixture (see <c>RealMachineFixtureTests</c>): what it offers for a PATH
/// that grew by itself, and what the recommended fixes would leave. Pure: nothing here applies anything.
/// </summary>
public class RealMachinePlanTests
{
    static readonly PathSnapshot Snapshot = PathSnapshotJson.Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dev-machine.redacted.json"))
                                            ?? throw new InvalidOperationException("fixture missing");

    static readonly Diagnosis Diagnosis = Diagnoser.Diagnose(Snapshot);
    static readonly IReadOnlyList<SuggestedFix> Fixes = RemediationPlanner.Suggest(Diagnosis, new DetectionContext(Snapshot));

    static PlanOutcome Recommended()
    {
        var chosen = Fixes.Where(f => f.Recommended).ToList();
        var draft = PathDraft.From(Snapshot).Apply(chosen.SelectMany(f => f.Edits));
        var acls = AclPlanner.Plan(Snapshot, chosen.SelectMany(f => f.Acls), new FakeDesigner());
        return PlanProjection.Project(Diagnosis, ChangeSet.From(Snapshot, draft, acls), new LockedEvaluator());
    }

    [Fact]
    public void Every_high_problem_with_a_fix_gets_one_and_the_rest_are_advisory()
    {
        var fixedCauses = Fixes.SelectMany(f => f.RootCauses).ToHashSet();
        var unfixed = Diagnosis.Groups.Where(g => g.Severity == Severity.High && !fixedCauses.Contains(g.RootCause))
            .Select(g => g.Primary.Rule).Distinct().Order().ToList();

        // The length limit is fixed by removing things, not by one edit of its own.
        Assert.Equal(["COR-08"], unfixed);
    }

    [Fact]
    public void The_drive_root_problem_locks_down_every_folder_it_reaches_in_one_fix()
    {
        var fix = Assert.Single(Fixes, f => f.RootCauses.Contains(@"inherited:C:\"));

        Assert.Equal(7, fix.Acls.Count);
        Assert.All(fix.Acls, a => Assert.True(a.StripWrite));
        Assert.True(fix.NeedsAdmin);
    }

    [Fact]
    public void The_recommended_fixes_take_security_and_correctness_off_high()
    {
        var outcome = Recommended();

        Assert.NotEqual(Severity.High, outcome.After.Health[FindingCategory.Security].Rating);
        Assert.True(outcome.After.Health[FindingCategory.Hygiene].IsClean);
        Assert.Empty(outcome.Introduced.Where(g => g.Severity > Severity.Low));
        Assert.Contains(outcome.Resolved, g => g.RootCause == @"inherited:C:\");
    }

    [Fact]
    public void Nested_folders_under_the_drive_root_are_carried_by_inheritance()
    {
        var chosen = Fixes.Single(f => f.RootCauses.Contains(@"inherited:C:\"));
        var changes = AclPlanner.Plan(Snapshot, chosen.Acls, new FakeDesigner());

        // C:\scripts\aliases sits under C:\scripts, so its lock-down comes through C:\scripts.
        Assert.Contains(changes, c => c.CoveredBy is not null && c.Mode == AclDesignMode.InheritedOnly);
        Assert.All(changes.Where(c => c.CoveredBy is null), c => Assert.Equal(AclDesignMode.Protect, c.Mode));
    }

    [Fact]
    public void Planning_never_changes_the_value_kind_unless_asked()
    {
        var chosen = Fixes.Where(f => f.Recommended && !f.Edits.OfType<SetKind>().Any());
        var draft = PathDraft.From(Snapshot).Apply(chosen.SelectMany(f => f.Edits));

        // The user PATH here really is REG_SZ, and stays so.
        Assert.Equal(PathValueKind.String, draft.UserKind);
        Assert.Equal(PathValueKind.ExpandString, draft.MachineKind);
    }
}
