using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Remediation;
using Pathology.Tests.Detection;

namespace Pathology.Tests.Remediation;

public class RemediationPlannerTests
{
    /// <summary>
    /// A writable C:\Tools ahead of System32 (and in twice), a phantom C:\Gone, a variable only the user defines,
    /// and a quoted user entry.
    /// </summary>
    internal static TestMachine Messy()
    {
        var m = new TestMachine(@"C:\Tools;%SystemRoot%\system32;%SystemRoot%;C:\Gone;%NVM%\bin;C:\Tools",
            @"""C:\Users\you\bin""");
        m.Registry.User["NVM"] = @"C:\Users\you\nvm";
        m.Environment.NewProcess["NVM"] = @"C:\Users\you\nvm";
        m.Folder(@"C:\", f => f.FoldersCreatableByEveryone());
        m.Folder(@"C:\Tools", f => f.WritableByEveryone().Files("where.bat", "jq.exe"));
        m.Folder(@"C:\Users\you", f => f.WritableByYou());
        m.Folder(@"C:\Users\you\bin", f => f.WritableByYou().Files("jq.exe"));
        m.Folder(@"C:\Users\you\nvm\bin", f => f.WritableByYou().Files("node.exe"));
        return m;
    }

    internal static (Diagnosis Diagnosis, DetectionContext Context, IReadOnlyList<SuggestedFix> Fixes) Suggest(TestMachine machine)
    {
        var snapshot = machine.Snapshot();
        var diagnosis = Diagnoser.Diagnose(snapshot);
        var context = new DetectionContext(snapshot);
        return (diagnosis, context, RemediationPlanner.Suggest(diagnosis, context));
    }

    static SuggestedFix FixFor(IReadOnlyList<SuggestedFix> fixes, Diagnosis diagnosis, string rule)
    {
        var group = diagnosis.Groups.First(g => g.Members.Any(f => f.Rule == rule));
        return Assert.Single(fixes, f => f.RootCauses.Contains(group.RootCause));
    }

    [Fact]
    public void A_phantom_folder_is_removed()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());
        var fix = FixFor(fixes, diagnosis, "SEC-03");

        Assert.Equal([new RemoveEntry(3)], fix.Edits);
        Assert.True(fix.Recommended);
        Assert.True(fix.NeedsAdmin);
        Assert.Equal(Severity.High, fix.Severity);
        Assert.Contains(@"C:\Gone", fix.Title);
    }

    [Fact]
    public void A_writable_machine_folder_is_locked_down_to_administrators()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());
        var fix = FixFor(fixes, diagnosis, "SEC-01");

        var acl = Assert.Single(fix.Acls);
        Assert.Equal(@"C:\Tools", acl.Folder);
        Assert.Equal(PathScope.Machine, acl.Scope);
        Assert.True(acl.StripWrite);
        Assert.Null(acl.KeepWriteSid);
        Assert.Empty(fix.Edits);
        Assert.NotNull(fix.Note);
    }

    [Fact]
    public void An_entry_using_a_user_only_variable_moves_to_the_user_path()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());
        var fix = FixFor(fixes, diagnosis, "COR-01");

        Assert.Equal([new MoveEntry(4, PathScope.User, 0)], fix.Edits);
        Assert.True(fix.NeedsAdmin);
    }

    [Fact]
    public void An_entry_your_user_path_already_has_is_removed_rather_than_moved()
    {
        var machine = new TestMachine(@"C:\Tools;%SystemRoot%\system32;%SystemRoot%;C:\Gone;%NVM%\bin;C:\Tools",
            @"""C:\Users\you\bin"";C:\Users\you\nvm\bin");
        machine.Registry.User["NVM"] = @"C:\Users\you\nvm";
        machine.Environment.NewProcess["NVM"] = @"C:\Users\you\nvm";
        machine.Folder(@"C:\Users\you\nvm\bin");
        var (diagnosis, _, fixes) = Suggest(machine);

        var fix = FixFor(fixes, diagnosis, "COR-01");
        Assert.Equal([new RemoveEntry(4)], fix.Edits);
        Assert.Contains(@"C:\Users\you\nvm\bin", fix.Note);
    }

    [Fact]
    public void Duplicates_keep_the_first_copy()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());

        Assert.Equal([new RemoveEntry(5)], FixFor(fixes, diagnosis, "COR-06").Edits);
    }

    [Fact]
    public void Quotes_are_tidied_as_you()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());
        var fix = FixFor(fixes, diagnosis, "HYG-01");

        Assert.Equal([new TidyText(6, HygieneDefects.Quotes)], fix.Edits);
        Assert.False(fix.NeedsAdmin);
    }

    [Fact]
    public void Putting_Windows_first_is_offered_but_not_ticked()
    {
        var (_, _, fixes) = Suggest(Messy());
        var fix = Assert.Single(fixes, f => f.Id == RemediationPlanner.WindowsFirstId);

        Assert.False(fix.Recommended);
        var reorder = Assert.IsType<Reorder>(Assert.Single(fix.Edits));
        Assert.Equal(PathScope.Machine, reorder.Scope);
        Assert.Equal([1, 2], reorder.Ids);
        Assert.Equal(Severity.Medium, fix.Severity);
    }

    [Fact]
    public void Windows_first_is_not_offered_when_they_already_are()
    {
        var (_, _, fixes) = Suggest(new TestMachine(@"%SystemRoot%\system32;%SystemRoot%;C:\Tools"));

        Assert.DoesNotContain(fixes, f => f.Id == RemediationPlanner.WindowsFirstId);
    }

    [Fact]
    public void A_non_admin_owner_is_reset()
    {
        var m = new TestMachine(@"%SystemRoot%\system32;C:\Mine");
        m.Folder(@"C:\Mine", f => f.OwnedBy(TestMachine.You));
        var (diagnosis, _, fixes) = Suggest(m);

        var acl = Assert.Single(FixFor(fixes, diagnosis, "SEC-02").Acls);
        Assert.True(acl.ResetOwner);
        Assert.False(acl.StripWrite);
    }

    [Fact]
    public void A_REG_SZ_value_with_variables_becomes_REG_EXPAND_SZ()
    {
        var m = new TestMachine(@"%SystemRoot%\system32", @"%USERPROFILE%\bin", userKind: PathValueKind.String);
        var (diagnosis, _, fixes) = Suggest(m);
        var fix = FixFor(fixes, diagnosis, "COR-02");

        Assert.Equal([new SetKind(PathScope.User, PathValueKind.ExpandString)], fix.Edits);
        Assert.False(fix.NeedsAdmin);
    }

    [Fact]
    public void Empty_entries_are_removed()
    {
        var (diagnosis, _, fixes) = Suggest(new TestMachine(@"%SystemRoot%\system32;;%SystemRoot%"));

        Assert.Equal([new RemoveEntry(1)], FixFor(fixes, diagnosis, "COR-03").Edits);
    }

    [Fact]
    public void A_missing_System32_is_put_back_first_and_expandable()
    {
        var (diagnosis, _, fixes) = Suggest(new TestMachine(@"C:\Tools", machineKind: PathValueKind.String));
        var fix = FixFor(fixes, diagnosis, "COR-09");

        var draft = PathDraft.From(diagnosis.Snapshot).Apply(fix.Edits);
        Assert.Equal(PathValueKind.ExpandString, draft.MachineKind);
        Assert.StartsWith(@"%SystemRoot%\system32;%SystemRoot%;", draft.ValueOf(PathScope.Machine));
        Assert.EndsWith(@";C:\Tools", draft.ValueOf(PathScope.Machine));
    }

    [Fact]
    public void A_user_folder_other_users_can_write_is_locked_to_you()
    {
        var m = new TestMachine(@"%SystemRoot%\system32", @"C:\Shared");
        m.Folder(@"C:\Shared", f => f.WritableByEveryone());
        var (diagnosis, _, fixes) = Suggest(m);

        var acl = Assert.Single(FixFor(fixes, diagnosis, "SEC-06").Acls);
        Assert.Equal(PathScope.User, acl.Scope);
        Assert.Equal(TestMachine.You, acl.KeepWriteSid);
    }

    [Fact]
    public void Advisory_problems_get_no_fix()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());

        Assert.Contains(diagnosis.Groups, g => g.RootCause == "uac-exposure");
        Assert.DoesNotContain(fixes, f => f.RootCauses.Contains("uac-exposure"));
    }

    [Fact]
    public void Notes_get_no_fix()
    {
        var (diagnosis, _, fixes) = Suggest(Messy());
        var notes = diagnosis.Groups.Where(g => g.Severity == Severity.Info).Select(g => g.RootCause).ToHashSet();

        Assert.DoesNotContain(fixes, f => f.RootCauses.Any(notes.Contains));
    }

    [Fact]
    public void Fix_ids_are_stable_across_scans()
    {
        var first = Suggest(Messy()).Fixes.Select(f => f.Id);
        var second = Suggest(Messy()).Fixes.Select(f => f.Id);

        Assert.Equal(first, second);
        Assert.Equal(first.Count(), first.Distinct().Count());
    }

    [Fact]
    public void The_recommended_order_is_Windows_then_locked_then_writable()
    {
        var m = new TestMachine(@"C:\Tools;C:\Program Files\Git\cmd;%SystemRoot%\system32");
        m.Folder(@"C:\Tools", f => f.WritableByEveryone());
        m.Folder(@"C:\Program Files\Git\cmd");
        var (diagnosis, context, _) = Suggest(m);
        var draft = PathDraft.From(diagnosis.Snapshot);

        Assert.Equal(new[] { 2, 1, 0 }, RemediationPlanner.RecommendedOrder(draft, PathScope.Machine, context).Ids);
        // Once C:\Tools is being locked down, it counts as locked: order among equals is kept.
        Assert.Equal(new[] { 2, 0, 1 }, RemediationPlanner.RecommendedOrder(draft, PathScope.Machine, context, [@"C:\Tools"]).Ids);
    }
}
