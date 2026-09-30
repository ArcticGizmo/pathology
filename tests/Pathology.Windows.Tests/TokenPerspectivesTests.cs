using Pathology.Core.Model;
using Pathology.Windows.Native;

namespace Pathology.Windows.Tests;

/// <summary>Reads this process's own token (a query; nothing is adjusted or elevated).</summary>
public class TokenPerspectivesTests
{
    readonly IReadOnlyList<PerspectiveIdentity> _perspectives = new TokenPerspectives().Build();

    [Fact]
    public void All_four_perspectives_come_back_in_order()
    {
        Assert.Equal(Enum.GetValues<Perspective>(), _perspectives.Select(p => p.Perspective));
    }

    [Fact]
    public void The_current_user_perspectives_are_this_account()
    {
        Quiet.Same(TempTree.CurrentUserSid, _perspectives[0].UserSid, "the unelevated user SID");
        Quiet.Same(TempTree.CurrentUserSid, _perspectives[1].UserSid, "the elevated user SID");
        Assert.False(_perspectives[0].Synthetic);
        Assert.Contains(_perspectives[0].Groups, g => g.Sid.StartsWith("S-1-16-", StringComparison.Ordinal));
    }

    [Fact]
    public void UAC_splits_Administrators_between_the_two_views()
    {
        using var token = TokenReader.OpenCurrent();
        if (TokenReader.Elevation(token) is not (ElevationType.Limited or ElevationType.Full))
            return;   // no split token here (UAC off, or not an admin): the two views are the same by design

        var unelevated = _perspectives[0].Groups.Single(g => g.Sid == WellKnownSids.Administrators);
        var elevated = _perspectives[1].Groups.Single(g => g.Sid == WellKnownSids.Administrators);
        Assert.True(unelevated.Attributes.HasFlag(GroupAttributes.DenyOnly));
        Assert.False(unelevated.MatchesAllowAces);
        Assert.True(elevated.MatchesAllowAces);
    }

    [Fact]
    public void SYSTEM_and_the_standard_user_are_fixed_sets()
    {
        var system = _perspectives[2];
        Assert.Equal(WellKnownSids.LocalSystem, system.UserSid);
        Assert.Contains(system.Groups, g => g.Sid == WellKnownSids.Administrators && g.MatchesAllowAces);

        var standard = _perspectives[3];
        Assert.Equal(WellKnownSids.SyntheticStandardUser, standard.UserSid);
        Assert.True(standard.Synthetic);
        Assert.DoesNotContain(standard.Groups, g => g.Sid == WellKnownSids.Administrators);
        Assert.Contains(standard.Groups, g => g.Sid == WellKnownSids.Users);
    }
}
