using Pathology.Core.Model;

namespace Pathology.Windows.Tests;

/// <summary>
/// The access check against crafted security descriptors (SDDL only; no file system), for identities whose
/// SIDs are made up. Each case is one of the rules the kernel applies that a naive ACE scan would get wrong.
/// </summary>
public sealed class AccessEvaluatorTests : IDisposable
{
    const string Alice = "S-1-5-21-1-2-3-1001";
    const string Admin = "S-1-5-21-1-2-3-500";
    const GroupAttributes On = GroupAttributes.Mandatory | GroupAttributes.EnabledByDefault | GroupAttributes.Enabled;
    const GroupAttributes Label = GroupAttributes.Integrity | GroupAttributes.IntegrityEnabled;

    readonly AccessEvaluator _sut = new();

    static readonly PerspectiveIdentity StandardAlice = new()
    {
        Perspective = Perspective.CurrentUserUnelevated,
        UserSid = Alice,
        Groups = [new(WellKnownSids.Everyone, On), new(WellKnownSids.Users, On), new(WellKnownSids.AuthenticatedUsers, On), new(WellKnownSids.MediumIntegrity, Label)],
    };

    static readonly PerspectiveIdentity FilteredAdmin = new()
    {
        Perspective = Perspective.CurrentUserUnelevated,
        UserSid = Admin,
        Groups = [new(WellKnownSids.Administrators, GroupAttributes.DenyOnly), new(WellKnownSids.Everyone, On), new(WellKnownSids.Users, On), new(WellKnownSids.MediumIntegrity, Label)],
    };

    static readonly PerspectiveIdentity ElevatedAdmin = new()
    {
        Perspective = Perspective.CurrentUserElevated,
        UserSid = Admin,
        Groups = [new(WellKnownSids.Administrators, On | GroupAttributes.Owner), new(WellKnownSids.Everyone, On), new(WellKnownSids.Users, On), new(WellKnownSids.HighIntegrity, Label)],
    };

    static PerspectiveIdentity Standard => TokenPerspectives.StandardUser;

    [Fact]
    public void A_folder_Users_can_modify_is_plantable_by_a_standard_user()
    {
        var result = _sut.Evaluate("O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x1301bf;;;BU)", Standard);

        Assert.True(result.CanAddFiles);
        Assert.True(result.CanAddSubdirectories);
        Assert.False(result.CanWriteDac);
        var ace = Assert.Single(result.GrantedBy);
        Assert.Equal(WellKnownSids.Users, ace.Sid);
        Assert.False(ace.Inherited);
    }

    [Fact]
    public void A_locked_down_folder_is_not_plantable_by_a_standard_user_or_a_filtered_admin()
    {
        const string sddl = "O:SYG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)";

        Assert.False(_sut.Evaluate(sddl, Standard).CanPlant);
        // UAC's filtered token carries Administrators as deny-only, so the BA grant doesn't count...
        Assert.False(_sut.Evaluate(sddl, FilteredAdmin).CanPlant);
        // ...until it elevates.
        Assert.True(_sut.Evaluate(sddl, ElevatedAdmin).CanAddFiles);
        Assert.True(_sut.Evaluate(sddl, TokenPerspectives.System).CanAddFiles);
    }

    [Fact]
    public void The_owner_gets_WRITE_DAC_from_ownership_alone()
    {
        var result = _sut.Evaluate($"O:{Alice}G:SYD:PAI(A;;FA;;;SY)(A;;0x1200a9;;;BU)", StandardAlice);

        Assert.False(result.CanAddFiles);
        Assert.True(result.CanWriteDac);
        Assert.True(result.OwnerInPerspective);
        Assert.True(result.WriteDacFromOwnership);
        Assert.Empty(result.GrantedBy);
    }

    [Fact]
    public void An_OWNER_RIGHTS_ACE_replaces_the_owner_implied_rights()
    {
        var result = _sut.Evaluate($"O:{Alice}G:SYD:PAI(A;;FA;;;SY)(A;;0x1200a9;;;OW)", StandardAlice);

        Assert.True(result.OwnerInPerspective);
        Assert.False(result.CanWriteDac);
        Assert.False(result.WriteDacFromOwnership);
    }

    [Fact]
    public void A_deny_ACE_overrides_a_later_allow()
    {
        var result = _sut.Evaluate("O:BAG:SYD:PAI(D;;0x2;;;BU)(A;;FA;;;WD)", Standard);

        Assert.False(result.CanAddFiles);
        Assert.True(result.CanAddSubdirectories);
    }

    [Fact]
    public void An_inherited_grant_is_marked_as_inherited()
    {
        var result = _sut.Evaluate("O:BAG:SYD:AI(A;ID;0x1301bf;;;AU)(A;OICIID;FA;;;SY)", Standard);

        Assert.True(result.CanAddFiles);
        var ace = Assert.Single(result.GrantedBy);
        Assert.Equal(WellKnownSids.AuthenticatedUsers, ace.Sid);
        Assert.True(ace.Inherited);
    }

    [Fact]
    public void An_inherit_only_ACE_does_not_apply_to_the_folder_itself()
    {
        var result = _sut.Evaluate("O:BAG:SYD:PAI(A;OICIIO;FA;;;BU)(A;;FA;;;SY)", Standard);

        Assert.False(result.CanPlant);
    }

    [Fact]
    public void A_high_integrity_label_stops_medium_integrity_writes()
    {
        const string sddl = "O:BAG:SYD:PAI(A;;FA;;;WD)S:(ML;;NW;;;HI)";

        Assert.False(_sut.Evaluate(sddl, Standard).CanAddFiles);
        Assert.True(_sut.Evaluate(sddl, ElevatedAdmin).CanAddFiles);
    }

    [Fact]
    public void Low_integrity_can_write_only_a_folder_labelled_Low()
    {
        const string acl = $"O:BAG:SYD:PAI(A;;FA;;;SY)(A;OICI;FA;;;{Alice})";
        var sandboxed = TokenPerspectives.Sandboxed(StandardAlice);

        Assert.True(_sut.Evaluate(acl, StandardAlice).CanAddFiles);
        // No label counts as Medium, which Low can't write up to.
        Assert.False(_sut.Evaluate(acl, sandboxed).CanPlant);
        Assert.False(_sut.Evaluate(acl + "S:(ML;;NW;;;ME)", sandboxed).CanPlant);
        Assert.True(_sut.Evaluate(acl + "S:(ML;OICI;NW;;;LW)", sandboxed).CanAddFiles);
        // An inherit-only label is for the children; the folder itself is still Medium.
        Assert.False(_sut.Evaluate(acl + "S:(ML;OICIIO;NW;;;LW)", sandboxed).CanPlant);
        // The label doesn't change what the Medium view can do.
        Assert.True(_sut.Evaluate(acl + "S:(ML;OICI;NW;;;LW)", StandardAlice).CanAddFiles);
    }

    [Fact]
    public void Nothing_granted_is_an_empty_result_not_an_error()
    {
        var result = _sut.Evaluate("O:SYG:SYD:P(A;;FA;;;SY)", Standard);

        Assert.True(result.Evaluated);
        Assert.Equal(FileAccessRights.None, result.Granted);
    }

    [Fact]
    public void A_malformed_descriptor_throws()
    {
        Assert.ThrowsAny<Exception>(() => _sut.Evaluate("not sddl", Standard));
    }

    public void Dispose() => _sut.Dispose();
}
