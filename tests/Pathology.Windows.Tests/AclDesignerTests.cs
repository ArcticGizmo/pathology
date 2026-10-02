using System.Security.AccessControl;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Windows.Tests;

/// <summary>
/// Lock-down designs over crafted SDDL (no file system), each checked with the real access evaluator: after the
/// design, a standard user can't plant a file, and nothing that should keep its access lost it.
/// </summary>
public sealed class AclDesignerTests : IDisposable
{
    const string Alice = "S-1-5-21-1-2-3-1001";

    /// <summary>A folder made directly under C:\: everything inherited, Authenticated Users may modify.</summary>
    const string DriveRootChild =
        "O:S-1-5-21-1-2-3-500G:S-1-5-21-1-2-3-513D:AI(A;ID;0x1301bf;;;AU)(A;OICIIOID;SDGXGWGR;;;AU)(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;OICIID;0x1200a9;;;BU)";

    readonly AclDesigner _sut = new();
    readonly AccessEvaluator _evaluator = new();

    static AclFix Machine => new() { Folder = @"C:\Tools", Scope = PathScope.Machine, StripWrite = true };

    bool StandardUserCanPlant(string sddl) => _evaluator.Evaluate(sddl, TokenPerspectives.StandardUser).CanPlant;

    [Fact]
    public void A_drive_root_child_is_protected_and_trimmed_to_read_and_execute()
    {
        Assert.True(StandardUserCanPlant(DriveRootChild));

        var design = _sut.Design(@"C:\Tools", DriveRootChild, Machine, AclDesignMode.Protect);

        Assert.Null(design.Refusal);
        var after = new RawSecurityDescriptor(design.Sddl!);
        Assert.True(after.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.All(after.DiscretionaryAcl!.Cast<GenericAce>(), a => Assert.False(a.IsInherited));
        Assert.False(StandardUserCanPlant(design.Sddl!));
        Assert.Contains("Authenticated Users: modify → read & execute", design.Lines);
        Assert.Contains(design.Commands, c => c.EndsWith("/inheritance:d", StringComparison.Ordinal));
        Assert.Contains(design.Commands, c => c.Contains("/grant:r *S-1-5-11:RX", StringComparison.Ordinal));
    }

    [Fact]
    public void Administrators_and_SYSTEM_keep_full_control_and_Users_keep_read()
    {
        var design = _sut.Design(@"C:\Tools", DriveRootChild, Machine, AclDesignMode.Protect);
        var sddl = design.Sddl!;

        var system = _evaluator.Evaluate(sddl, new PerspectiveIdentity
        {
            Perspective = Perspective.System, UserSid = WellKnownSids.LocalSystem,
            Groups = [new(WellKnownSids.Administrators, GroupAttributes.Enabled)],
        });
        Assert.True(system.CanAddFiles && system.CanWriteDac);
        Assert.Contains("(A;OICI;0x1200a9;;;BU)", sddl);
        Assert.DoesNotContain(design.Lines, l => l.StartsWith("SYSTEM", StringComparison.Ordinal) || l.StartsWith("Administrators", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_admin_entries_are_added()
    {
        var design = _sut.Design(@"C:\Tools", "O:BAG:SYD:(A;OICI;FA;;;WD)", Machine, AclDesignMode.Protect);

        Assert.False(StandardUserCanPlant(design.Sddl!));
        Assert.Contains("Everyone: full control → read & execute", design.Lines);
        Assert.Contains(design.Lines, l => l.StartsWith("SYSTEM: full control (added", StringComparison.Ordinal));
        Assert.Contains(design.Lines, l => l.StartsWith("Administrators: full control (added", StringComparison.Ordinal));
    }

    [Fact]
    public void Administrative_trustees_are_never_trimmed()
    {
        const string sddl = "O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;OICIIO;GA;;;CO)(A;OICI;0x1200a9;;;BU)";

        var design = _sut.Design(@"C:\Tools", sddl, Machine, AclDesignMode.Protect);

        Assert.Empty(design.Lines);
        Assert.Empty(design.Commands);
    }

    [Fact]
    public void Deny_entries_survive_in_front()
    {
        var design = _sut.Design(@"C:\Tools", "O:BAG:SYD:(A;OICI;FA;;;BU)(D;OICI;0x2;;;S-1-5-21-1-2-3-1077)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", Machine, AclDesignMode.Protect);

        var first = new RawSecurityDescriptor(design.Sddl!).DiscretionaryAcl![0] as CommonAce;
        Assert.Equal(AceQualifier.AccessDenied, first!.AceQualifier);
    }

    [Fact]
    public void A_new_owner_is_Administrators()
    {
        var fix = Machine with { StripWrite = false, ResetOwner = true };
        var design = _sut.Design(@"C:\Mine", $"O:{Alice}G:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)", fix, AclDesignMode.OwnEntriesOnly);

        Assert.Equal(WellKnownSids.Administrators, design.OwnerSid);
        Assert.Equal(WellKnownSids.Administrators, new RawSecurityDescriptor(design.Sddl!).Owner!.Value);
        Assert.Equal($"Owner: {Alice} → Administrators", Assert.Single(design.Lines));
        Assert.Contains(design.Commands, c => c.EndsWith("/setowner *S-1-5-32-544", StringComparison.Ordinal));
    }

    [Fact]
    public void Your_own_folder_keeps_your_write_access_and_loses_everyone_elses()
    {
        var fix = new AclFix { Folder = @"C:\Shared", Scope = PathScope.User, StripWrite = true, KeepWriteSid = Alice };
        var design = _sut.Design(@"C:\Shared", DriveRootChild, fix, AclDesignMode.Protect);

        Assert.False(StandardUserCanPlant(design.Sddl!));
        var you = _evaluator.Evaluate(design.Sddl!, new PerspectiveIdentity
        {
            Perspective = Perspective.CurrentUserUnelevated, UserSid = Alice,
            Groups = [new(WellKnownSids.Users, GroupAttributes.Enabled), new(WellKnownSids.AuthenticatedUsers, GroupAttributes.Enabled)],
        });
        Assert.True(you.CanAddFiles);
        Assert.Contains("You: modify (added, so you keep the access you had)", design.Lines);
    }

    [Fact]
    public void Inside_a_locked_folder_only_its_own_entries_change_and_inheritance_stays_on()
    {
        const string sddl = "O:BAG:SYD:AI(A;OICI;FA;;;WD)(A;ID;0x1301bf;;;AU)(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)";

        var design = _sut.Design(@"C:\Py\Lib", sddl, Machine, AclDesignMode.OwnEntriesOnly);

        var after = new RawSecurityDescriptor(design.Sddl!);
        Assert.False(after.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.False(StandardUserCanPlant(design.Sddl!));
        Assert.Contains("Everyone: full control → read & execute", design.Lines);
        Assert.Contains("Authenticated Users: modify → read & execute (through inheritance)", design.Lines);
        Assert.DoesNotContain(design.Commands, c => c.Contains("inheritance", StringComparison.Ordinal) || c.Contains("S-1-5-11", StringComparison.Ordinal));
    }

    [Fact]
    public void Inherited_only_is_described_but_has_nothing_to_run()
    {
        var design = _sut.Design(@"C:\Py\Scripts", DriveRootChild, Machine, AclDesignMode.InheritedOnly);

        Assert.False(StandardUserCanPlant(design.Sddl!));
        Assert.Empty(design.Commands);
        Assert.All(design.Lines, l => Assert.EndsWith("(through inheritance)", l));
    }

    [Fact]
    public void Unusual_entries_are_refused_not_rewritten()
    {
        var design = _sut.Design(@"C:\Tools", "O:BAG:SYD:(OA;;CC;bf967aba-0de6-11d0-a285-00aa003049e2;;WD)(A;;FA;;;BU)", Machine, AclDesignMode.Protect);

        Assert.NotNull(design.Refusal);
        Assert.Null(design.Sddl);
    }

    [Fact]
    public void A_null_DACL_is_replaced_by_a_locked_one()
    {
        var design = _sut.Design(@"C:\Tools", "O:BAG:SYD:NO_ACCESS_CONTROL", Machine, AclDesignMode.Protect);

        Assert.Null(design.Refusal);
        Assert.False(StandardUserCanPlant(design.Sddl!));
    }

    public void Dispose() => _evaluator.Dispose();
}
