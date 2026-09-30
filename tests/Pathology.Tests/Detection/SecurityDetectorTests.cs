using Pathology.Core.Detection;
using Pathology.Core.Model;
using static Pathology.Tests.Detection.TestMachine;

namespace Pathology.Tests.Detection;

public class SecurityDetectorTests
{
    const string Windows = @"%SystemRoot%\system32;%SystemRoot%";

    // SEC-01 ------------------------------------------------------------------------------------------------

    [Fact]
    public void SEC01_a_machine_folder_any_user_can_write_is_critical()
    {
        var machine = new TestMachine($@"{Windows};C:\Tools").Folder(@"C:\Tools", f => f.WritableByEveryone());

        var finding = machine.Single("SEC-01");

        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Equal(@"C:\Tools", finding.Subject);
        Assert.Contains(finding.Evidence, e => e.Contains("Users: create files, create folders"));
        Assert.Contains(Perspective.System, finding.Perspectives);
    }

    [Theory]
    [InlineData(true, Severity.High)]      // an admin could elevate anyway: a UAC bypass
    [InlineData(false, Severity.Critical)] // a standard user reaching SYSTEM: an escalation
    public void SEC01_a_machine_folder_only_you_can_write_depends_on_whether_you_are_an_admin(bool admin, Severity expected)
    {
        var machine = new TestMachine($@"{Windows};C:\Users\you\tools") { Admin = admin }
            .Folder(@"C:\Users\you\tools", f => f.WritableByYou());

        var finding = machine.Single("SEC-01");

        Assert.Equal(expected, finding.Severity);
        Assert.Contains(finding.Evidence, e => e.Contains("you: create files"));
    }

    [Fact]
    public void SEC01_ignores_locked_folders_user_scope_and_ownership_only_access()
    {
        var machine = new TestMachine($@"{Windows};C:\Locked;C:\Mine", @"C:\Users\you\bin")
            .Folder(@"C:\Locked")
            .Folder(@"C:\Mine", f => f.OwnedBy(You))
            .Folder(@"C:\Users\you\bin", f => f.WritableByEveryone());

        Assert.Empty(machine.Findings("SEC-01"));
        // Owning C:\Mine is SEC-02's to explain.
        Assert.Single(machine.Findings("SEC-02"));
    }

    // SEC-02 ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(You, Severity.High)]
    [InlineData(WellKnownSids.Users, Severity.Critical)]
    [InlineData(SomeoneElse, Severity.High)]
    public void SEC02_a_machine_folder_owned_by_a_non_admin_is_flagged(string owner, Severity expected)
    {
        var machine = new TestMachine($@"{Windows};C:\Tools").Folder(@"C:\Tools", f => f.OwnedBy(owner));

        var finding = machine.Single("SEC-02");

        Assert.Equal(expected, finding.Severity);
        Assert.Contains("setowner Administrators", finding.Fix);
        Assert.Equal("owner:" + @"C:\TOOLS", finding.RootCause);
    }

    [Theory]
    [InlineData(WellKnownSids.Administrators)]
    [InlineData(WellKnownSids.TrustedInstaller)]
    [InlineData(WellKnownSids.LocalSystem)]
    [InlineData("S-1-5-21-111-222-333-500")]
    public void SEC02_administrative_owners_are_fine(string owner)
    {
        var machine = new TestMachine($@"{Windows};C:\Tools").Folder(@"C:\Tools", f => f.OwnedBy(owner));

        Assert.Empty(machine.Findings("SEC-02"));
    }

    [Theory]
    [InlineData(You)]
    [InlineData(SomeoneElse)]
    public void SEC02_an_OWNER_RIGHTS_entry_takes_the_owners_implicit_rights_away(string owner)
    {
        var machine = new TestMachine($@"{Windows};C:\Tools").Folder(@"C:\Tools", f => f.OwnedBy(owner, limitedByOwnerRights: true));

        Assert.Empty(machine.Findings("SEC-02"));
    }

    // SEC-03 ------------------------------------------------------------------------------------------------

    [Fact]
    public void SEC03_a_missing_machine_folder_anyone_can_create_is_a_critical_phantom()
    {
        var machine = new TestMachine($@"{Windows};C:\Android\sdk\platform-tools").Folder(@"C:\", f => f.FoldersCreatableByEveryone());

        var diagnosis = machine.Diagnose();

        var phantom = Assert.Single(diagnosis.Findings, f => f.Rule == "SEC-03");
        Assert.Equal(Severity.Critical, phantom.Severity);
        Assert.Contains(@"C:\", phantom.What);
        // It's the same problem as the dead entry: one group, led by the phantom.
        var group = Assert.Single(diagnosis.Groups, g => g.RootCause == phantom.RootCause);
        Assert.Same(phantom, group.Primary);
        Assert.Equal(["COR-05"], group.Related.Select(r => r.Rule));
    }

    [Fact]
    public void SEC03_a_missing_folder_under_a_locked_parent_is_only_dead()
    {
        var machine = new TestMachine($@"{Windows};C:\Program Files\Gone").Folder(@"C:\Program Files");

        Assert.Empty(machine.Findings("SEC-03"));
        Assert.Single(machine.Findings("COR-05"));
    }

    [Fact]
    public void SEC03_is_for_the_machine_PATH_a_missing_user_folder_is_SEC06()
    {
        var machine = new TestMachine(Windows, @"C:\Gone").Folder(@"C:\", f => f.FoldersCreatableByEveryone());

        Assert.Empty(machine.Findings("SEC-03"));
        Assert.Equal(Severity.Medium, machine.Single("SEC-06").Severity);
    }

    // SEC-04 ------------------------------------------------------------------------------------------------

    [Fact]
    public void SEC04_a_writable_folder_before_System32_can_shadow_built_ins()
    {
        var machine = new TestMachine($@"C:\Tools;{Windows}").Folder(@"C:\Tools", f => f.WritableByEveryone());

        var finding = machine.Single("SEC-04");

        Assert.Equal(Severity.High, finding.Severity);
        Assert.Contains("4 Windows commands", finding.Why);
        Assert.Contains("including cmd, where, net and notepad", finding.Why);
        // One fix (lock it down) covers both, so they share a root cause.
        Assert.Equal(machine.Single("SEC-01").RootCause, finding.RootCause);
    }

    [Fact]
    public void SEC04_writable_folders_after_System32_or_a_PATH_without_it_are_not_shadowing_risks()
    {
        Assert.Empty(new TestMachine($@"{Windows};C:\Tools").Folder(@"C:\Tools", f => f.WritableByEveryone()).Findings("SEC-04"));
        Assert.Empty(new TestMachine(@"C:\Tools").Folder(@"C:\Tools", f => f.WritableByEveryone()).Findings("SEC-04"));
    }

    // SEC-05 ------------------------------------------------------------------------------------------------

    [Fact]
    public void SEC05_folders_inheriting_the_drive_roots_write_access_share_one_root_cause()
    {
        var machine = new TestMachine($@"{Windows};C:\Tools;C:\Python\Scripts")
            .Folder(@"C:\Tools", f => f.WritableByEveryone(inherited: true, sid: WellKnownSids.AuthenticatedUsers))
            .Folder(@"C:\Python\Scripts", f => f.WritableByEveryone(inherited: true, sid: WellKnownSids.AuthenticatedUsers));

        var diagnosis = machine.Diagnose();

        var root = Assert.Single(diagnosis.Findings, f => f.Rule == "SEC-05");
        Assert.Equal(Severity.Critical, root.Severity);
        Assert.Equal(@"C:\", root.Subject);
        Assert.Equal(2, root.Entries.Count);
        // The per-folder SEC-01s are the same problem: one group, led by the root cause.
        var group = Assert.Single(diagnosis.Groups, g => g.RootCause == root.RootCause);
        Assert.Same(root, group.Primary);
        Assert.Equal(2, group.Related.Count(r => r.Rule == "SEC-01"));
    }

    [Fact]
    public void SEC05_ProgramData_is_its_own_source()
    {
        var machine = new TestMachine($@"{Windows};C:\ProgramData\tool\bin")
            .Folder(@"C:\ProgramData\tool\bin", f => f.WritableByEveryone(inherited: true));

        Assert.Equal(@"C:\ProgramData", machine.Single("SEC-05").Subject);
    }

    [Fact]
    public void SEC05_needs_every_grant_inherited_and_ignores_the_protected_trees()
    {
        var machine = new TestMachine($@"{Windows};C:\Explicit;C:\Program Files\Odd")
            .Folder(@"C:\Explicit", f => f.WritableByEveryone(inherited: false))
            .Folder(@"C:\Program Files\Odd", f => f.WritableByEveryone(inherited: true));

        Assert.Empty(machine.Findings("SEC-05"));
        Assert.All(machine.Findings("SEC-01"), f => Assert.StartsWith("acl:", f.RootCause));
    }

    // SEC-06 ------------------------------------------------------------------------------------------------

    [Fact]
    public void SEC06_a_user_folder_other_users_can_write_is_medium()
    {
        var machine = new TestMachine(Windows, @"C:\Shared\bin;C:\Users\you\bin")
            .Folder(@"C:\Shared\bin", f => f.WritableByEveryone())
            .Folder(@"C:\Users\you\bin", f => f.WritableByYou());

        var finding = machine.Single("SEC-06");

        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Equal(@"C:\Shared\bin", finding.Subject);
    }

    // SEC-07 ------------------------------------------------------------------------------------------------

    [Fact]
    public void SEC07_folders_you_can_write_unelevated_are_summarised_once()
    {
        var machine = new TestMachine(Windows, @"C:\Users\you\bin;C:\Users\you\.cargo\bin")
            .Folder(@"C:\Users\you\bin", f => f.WritableByYou())
            .Folder(@"C:\Users\you\.cargo\bin", f => f.WritableByYou());

        var finding = machine.Single("SEC-07");

        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Equal(2, finding.Entries.Count);
        Assert.Equal("uac-exposure", finding.RootCause);
    }

    [Fact]
    public void SEC07_the_stock_WindowsApps_folder_alone_is_only_information()
    {
        var machine = new TestMachine(Windows, @"%LOCALAPPDATA%\Microsoft\WindowsApps")
            .Folder(@"C:\Users\you\AppData\Local\Microsoft\WindowsApps", f => f.WritableByYou());

        Assert.Equal(Severity.Info, machine.Single("SEC-07").Severity);
    }

    [Fact]
    public void SEC07_needs_an_administrator_with_a_split_token()
    {
        var machine = new TestMachine(Windows, @"C:\Users\you\bin") { Admin = false }
            .Folder(@"C:\Users\you\bin", f => f.WritableByYou());

        Assert.Empty(machine.Findings("SEC-07"));
    }

    // SEC-08 ------------------------------------------------------------------------------------------------

    [Fact]
    public void SEC08_a_junction_to_a_writable_folder_is_judged_by_its_target()
    {
        var machine = new TestMachine($@"{Windows};C:\Program Files\Tool\bin")
            .Folder(@"C:\Program Files\Tool\bin", f => f.Shape(d => d with
            {
                Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint,
                ReparseTag = ReparseTags.MountPoint,
                ReparseTarget = @"D:\Open",
            }))
            .Folder(@"D:\Open", f => f.WritableByEveryone());

        var finding = machine.Single("SEC-08");

        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains("junction", finding.Title);
        Assert.Contains(@"C:\Program Files\Tool\bin → D:\Open", finding.Evidence);
        // The link's own (clean) folder isn't reported as SEC-01.
        Assert.Empty(machine.Findings("SEC-01"));
    }

    [Fact]
    public void SEC08_a_link_to_a_locked_folder_is_fine()
    {
        var machine = new TestMachine($@"{Windows};C:\Link")
            .Folder(@"C:\Link", f => f.Shape(d => d with { ReparseTag = ReparseTags.Symlink, ReparseTarget = @"C:\Program Files\Real" }))
            .Folder(@"C:\Program Files\Real");

        Assert.Empty(machine.Findings("SEC-08"));
    }

    // SEC-09 ------------------------------------------------------------------------------------------------

    [Fact]
    public void SEC09_a_UNC_entry_is_reported_without_being_probed()
    {
        var machine = new TestMachine($@"{Windows};\\fileserver\tools\bin");

        var finding = machine.Single("SEC-09");

        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Contains("network share", finding.Title);
        Assert.Contains(finding.Evidence, e => e.Contains(@"\\fileserver\tools") && e.Contains("computer's own account"));
    }

    [Theory]
    [InlineData(null, "isn't set")]
    [InlineData(true, "share the mapping")]
    public void SEC09_a_mapped_drive_explains_who_can_see_it(bool? linked, string expected)
    {
        var machine = new TestMachine($@"{Windows};Z:\tools") { EnableLinkedConnections = linked }
            .Folder(@"Z:\tools", f => f.Shape(d => d with { Drive = DriveKind.MappedNetwork, DriveTarget = @"\\nas\tools", Status = ProbeStatus.SkippedNetwork, Exists = false }));

        var finding = machine.Single("SEC-09");

        Assert.Contains(finding.Evidence, e => e.Contains(expected));
        Assert.Contains(finding.Evidence, e => e.Contains("SYSTEM and services never see it"));
    }

    [Fact]
    public void SEC09_an_unmounted_drive_letter_is_the_same_problem_as_the_dead_entry()
    {
        var machine = new TestMachine($@"{Windows};Q:\tools")
            .Folder(@"Q:\tools", f => f.Shape(d => d with { Drive = DriveKind.NoRootDir, Exists = false, IsDirectory = false }));

        var diagnosis = machine.Diagnose();

        var fragile = Assert.Single(diagnosis.Findings, f => f.Rule == "SEC-09");
        var group = Assert.Single(diagnosis.Groups, g => g.RootCause == fragile.RootCause);
        Assert.Same(fragile, group.Primary);
        Assert.Contains(group.Related, r => r.Rule == "COR-05");
    }

    [Fact]
    public void SEC09_ignores_fixed_drives()
    {
        Assert.Empty(new TestMachine($@"{Windows};C:\Program Files\Tool").Folder(@"C:\Program Files\Tool").Findings("SEC-09"));
    }
}
