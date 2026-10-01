using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Remediation;
using Pathology.Tests.Detection;
using static Pathology.Tests.Remediation.RemediationPlannerTests;

namespace Pathology.Tests.Remediation;

public class PlanProjectionTests
{
    static ChangeSet Changes(Diagnosis diagnosis, IEnumerable<SuggestedFix> fixes, IAclDesigner? designer = null)
    {
        var chosen = fixes.ToList();
        var draft = PathDraft.From(diagnosis.Snapshot).Apply(chosen.SelectMany(f => f.Edits));
        var acls = AclPlanner.Plan(diagnosis.Snapshot, chosen.SelectMany(f => f.Acls), designer ?? new FakeDesigner());
        return ChangeSet.From(diagnosis.Snapshot, draft, acls);
    }

    [Fact]
    public void No_changes_project_the_same_machine()
    {
        var (diagnosis, _, _) = Suggest(Messy());
        var outcome = PlanProjection.Project(diagnosis, new ChangeSet());

        Assert.Empty(outcome.Resolved);
        Assert.Empty(outcome.Introduced);
        Assert.Empty(outcome.Commands);
        Assert.Equal(diagnosis.Findings.Select(f => f.Key), outcome.After.Diagnosis.Findings.Select(f => f.Key));
    }

    [Fact]
    public void The_recommended_fixes_clear_what_they_were_for()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());
        var outcome = PlanProjection.Project(diagnosis, Changes(diagnosis, fixes.Where(f => f.Recommended)), new LockedEvaluator());

        Assert.Equal(Severity.High, outcome.BeforeHealth[FindingCategory.Security].Rating);
        // What's left is the UAC exposure summary, which has no automatic fix.
        Assert.Equal(Severity.Medium, outcome.After.Health[FindingCategory.Security].Rating);
        Assert.Equal("uac-exposure", Assert.Single(outcome.After.Health[FindingCategory.Security].Problems).RootCause);
        Assert.True(outcome.After.Health[FindingCategory.Correctness].IsClean);
        Assert.True(outcome.After.Health[FindingCategory.Hygiene].IsClean);
        Assert.DoesNotContain(outcome.Introduced, g => g.Severity > Severity.Info);
        Assert.Contains(outcome.Resolved, g => g.Members.Any(f => f.Rule == "SEC-03"));
    }

    [Fact]
    public void A_moved_entry_expands_in_its_new_scope()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());
        var move = Assert.Single(fixes, f => f.Edits.OfType<MoveToUser>().Any());
        var after = PlanProjection.Project(diagnosis, Changes(diagnosis, [move])).After.Snapshot;

        var moved = after.EntriesIn(PathScope.User).First();
        Assert.Equal(@"%NVM%\bin", moved.Raw);
        Assert.Equal(@"C:\Users\you\nvm\bin", moved.Expanded);
        Assert.Empty(moved.UnresolvedVariables);
    }

    [Fact]
    public void Putting_Windows_first_shows_the_built_in_that_wins_again()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());
        var outcome = PlanProjection.Project(diagnosis, Changes(diagnosis, fixes.Where(f => f.Id == RemediationPlanner.WindowsFirstId)));

        var where = Assert.Single(outcome.Commands, c => c.Command == "where");
        Assert.Equal(@"C:\Tools\where.bat", where.Before!.FullPath);
        Assert.Equal(@"C:\Windows\System32\where.exe", where.After!.FullPath, ignoreCase: true);
        Assert.True(where.Builtin);
        Assert.Equal("where", outcome.Commands[0].Command);
    }

    [Fact]
    public void Removing_a_folder_loses_its_commands()
    {
        var m = new TestMachine(@"%SystemRoot%\system32;C:\Old");
        m.Folder(@"C:\Old", f => f.Files("ancient.exe"));
        var snapshot = m.Snapshot();
        var diagnosis = Diagnoser.Diagnose(snapshot);
        var changes = ChangeSet.From(snapshot, PathDraft.From(snapshot).Apply(new RemoveEntry(1)));

        var change = Assert.Single(PlanProjection.Project(diagnosis, changes).Commands);
        Assert.Equal("ancient", change.Command);
        Assert.True(change.Gone);
    }

    [Fact]
    public void A_designed_lock_down_is_evaluated_and_clears_the_finding()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());
        var lockDown = fixes.Single(f => f.Acls.Any(a => a.Folder == @"C:\Tools"));
        var evaluator = new LockedEvaluator();
        var outcome = PlanProjection.Project(diagnosis, Changes(diagnosis, [lockDown]), evaluator);

        Assert.All(evaluator.Asked, sddl => Assert.EndsWith("|Protect", sddl));
        Assert.DoesNotContain(outcome.After.Diagnosis.Findings, f => f.Rule is "SEC-01" or "SEC-04");
    }

    [Fact]
    public void A_folder_the_scan_never_saw_is_probed_read_only_when_a_probe_is_given()
    {
        var snapshot = new TestMachine(@"%SystemRoot%\system32").Snapshot();
        var diagnosis = Diagnoser.Diagnose(snapshot);
        var changes = ChangeSet.From(snapshot,
            PathDraft.From(snapshot).Apply(new AddEntry(PathDraft.ManualIdBase, PathScope.Machine, 1, @"C:\Brand\New")));

        var blind = PlanProjection.Project(diagnosis, changes).After.Snapshot;
        Assert.Equal(ProbeStatus.Failed, blind.FactsFor(@"C:\Brand\New")!.Status);

        var probe = new RecordingProbe();
        var outcome = PlanProjection.Project(diagnosis, changes, new LockedEvaluator(), probe);
        Assert.Contains(@"C:\Brand\New", probe.Probed);
        Assert.Contains(outcome.Commands, c => c.Command == "new" && c.New);
    }

    [Fact]
    public void Nested_lock_downs_lock_the_outer_folder_and_let_inheritance_carry_the_rest()
    {
        var m = new TestMachine(@"%SystemRoot%\system32;C:\Py;C:\Py\Scripts;C:\Py\Lib");
        m.Folder(@"C:\", f => f.FoldersCreatableByEveryone());
        m.Folder(@"C:\Py", f => f.WritableByEveryone(inherited: true, WellKnownSids.AuthenticatedUsers));
        m.Folder(@"C:\Py\Scripts", f => f.WritableByEveryone(inherited: true, WellKnownSids.AuthenticatedUsers));
        m.Folder(@"C:\Py\Lib", f => f.WritableByEveryone(inherited: false));
        var snapshot = m.Snapshot();
        var fixes = new[] { @"C:\Py\Lib", @"C:\Py\Scripts", @"C:\Py" }
            .Select(p => new AclFix { Folder = p, Scope = PathScope.Machine, StripWrite = true });

        var changes = AclPlanner.Plan(snapshot, fixes, new FakeDesigner());

        Assert.Equal([@"C:\Py", @"C:\Py\Lib", @"C:\Py\Scripts"], changes.Select(c => c.Path));
        Assert.Equal([AclDesignMode.Protect, AclDesignMode.OwnEntriesOnly, AclDesignMode.InheritedOnly], changes.Select(c => c.Mode));
        Assert.Equal([null, @"C:\Py", @"C:\Py"], changes.Select(c => c.CoveredBy));
        Assert.Equal([true, true, false], changes.Select(c => c.Writes));
    }

    [Fact]
    public void Fixes_for_the_same_folder_merge_into_one_change()
    {
        var m = new TestMachine(@"%SystemRoot%\system32;C:\Mine");
        m.Folder(@"C:\Mine", f => f.OwnedBy(TestMachine.You).WritableByEveryone());
        var changes = AclPlanner.Plan(m.Snapshot(),
        [
            new AclFix { Folder = @"C:\Mine", Scope = PathScope.Machine, StripWrite = true },
            new AclFix { Folder = @"C:\Mine", Scope = PathScope.Machine, ResetOwner = true },
        ], new FakeDesigner());

        var change = Assert.Single(changes);
        Assert.Equal(AclDesignMode.Protect, change.Mode);
        Assert.Equal(WellKnownSids.Administrators, change.AfterOwnerSid);
        Assert.True(change.NeedsAdmin);
    }

    [Fact]
    public void Your_own_user_folder_is_changed_as_you_when_you_may_rewrite_its_permissions()
    {
        var m = new TestMachine(@"%SystemRoot%\system32", @"C:\Shared;C:\Theirs");
        m.Folder(@"C:\Shared", f => f.WritableByEveryone().WritableBy(Perspective.CurrentUserUnelevated, TestMachine.You, rights: FileAccessRights.WriteDac));
        m.Folder(@"C:\Theirs", f => f.WritableByEveryone());
        var changes = AclPlanner.Plan(m.Snapshot(),
            new[] { @"C:\Shared", @"C:\Theirs" }.Select(p => new AclFix { Folder = p, Scope = PathScope.User, StripWrite = true, KeepWriteSid = TestMachine.You }),
            new FakeDesigner());

        Assert.Equal([false, true], changes.Select(c => c.NeedsAdmin));
    }

    [Fact]
    public void A_folder_whose_permissions_were_not_read_is_refused_not_guessed()
    {
        var m = new TestMachine(@"%SystemRoot%\system32;C:\Locked");
        m.Folder(@"C:\Locked", f => f.Shape(x => x with { Sddl = null, SecurityError = "Access to the security descriptor was denied." }));

        var change = Assert.Single(AclPlanner.Plan(m.Snapshot(), [new AclFix { Folder = @"C:\Locked", StripWrite = true }], new FakeDesigner()));
        Assert.NotNull(change.Refusal);
        Assert.False(change.Writes);
    }

    [Fact]
    public void The_change_set_lists_only_values_that_change()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());
        var quotes = fixes.Single(f => f.Edits.OfType<TidyText>().Any());
        var changes = Changes(diagnosis, [quotes]);

        var value = Assert.Single(changes.Values);
        Assert.Equal(PathScope.User, value.Scope);
        Assert.Equal(@"C:\Users\you\bin", value.After.Value);
        Assert.False(changes.NeedsAdmin);
    }

    [Fact]
    public void A_value_past_the_limit_is_a_problem()
    {
        var snapshot = new TestMachine(@"%SystemRoot%\system32").Snapshot();
        var huge = PathDraft.From(snapshot).Apply(new AddEntry(PathDraft.ManualIdBase, PathScope.User, 0, new string('x', ChangeSet.MaxValueLength + 1)));

        Assert.Single(ChangeSet.From(snapshot, huge).Problems());
    }
}
