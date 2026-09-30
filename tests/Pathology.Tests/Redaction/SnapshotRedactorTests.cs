using Pathology.Core.Model;
using Pathology.Core.Redaction;
using Pathology.Tests.Capture;
using static Pathology.Tests.Capture.SampleSnapshot;

namespace Pathology.Tests.Redaction;

public class SnapshotRedactorTests
{
    static readonly PathSnapshot Redacted = SnapshotRedactor.Redact(Build());
    static readonly string Json = PathSnapshotJson.Serialize(Redacted);

    [Theory]
    [InlineData(User)]
    [InlineData(Machine)]
    [InlineData(Domain)]
    [InlineData(DomainSid)]
    [InlineData("1111111111")]
    [InlineData("ALEXEX~1")]
    [InlineData("sam.other")]
    [InlineData("fileserver01")]
    [InlineData("DC-EXAMPLE-01")]
    [InlineData("Example Corp")]
    [InlineData("example.com")]
    [InlineData("S-1-12-1-111")]
    [InlineData("555555555")]
    public void No_identifying_value_survives(string value) =>
        Assert.DoesNotContain(value, Json, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void The_leak_check_agrees()
    {
        Assert.Empty(SnapshotRedactor.FindLeaks(Json, Build().Host));
        Assert.NotEmpty(SnapshotRedactor.FindLeaks(PathSnapshotJson.Serialize(Build()), Build().Host));
    }

    [Fact]
    public void The_leak_check_names_categories_never_values()
    {
        var leaks = SnapshotRedactor.FindLeaks(PathSnapshotJson.Serialize(Build()), Build().Host);

        Assert.Contains("user SID", leaks);
        Assert.Contains("machine name", leaks);
        Assert.All(leaks, l => Assert.DoesNotContain(User, l, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Profile_paths_become_the_user_placeholder()
    {
        Assert.Equal(@"C:\Users\<user>", Redacted.Host.UserProfile);
        Assert.Equal(@"C:\Users\<user>\bin", Redacted.Entries[0].Expanded);
        Assert.Contains(@"C:\Users\<user>\AppData\Local\x", Redacted.UserPath.Value);
        Assert.Contains(@"OneDrive - <org>", Redacted.UserPath.Value);
        // Windows' own shared profile keeps its name.
        Assert.Equal(@"C:\Users\Public", Redacted.EnvironmentFrom(EnvironmentSource.NewProcess)!.ValueOf("PUBLIC"));
    }

    [Fact]
    public void Account_SIDs_keep_their_shape_and_stay_consistent()
    {
        var unelevated = Redacted.IdentityOf(Perspective.CurrentUserUnelevated)!;

        Assert.Equal("S-1-5-21-0-0-1-1104", Redacted.Host.UserSid);
        Assert.Equal(Redacted.Host.UserSid, unelevated.UserSid);
        Assert.Equal(Redacted.Host.UserSid, Redacted.Directories[0].OwnerSid);
        Assert.Equal(Redacted.Host.UserSid, Redacted.Directories[0].Access[0].GrantedBy[0].Sid);
        Assert.Contains("S-1-5-21-0-0-1-513", unelevated.Groups.Select(g => g.Sid));
        Assert.StartsWith("O:S-1-5-21-0-0-1-1104G:S-1-5-21-0-0-1-513D:", Redacted.Directories[0].Sddl);
        Assert.StartsWith("O:S-1-12-1-0-0-0-2G:S-1-12-1-0-0-0-2D:", Redacted.Directories[2].Sddl);
    }

    [Fact]
    public void Well_known_and_synthetic_SIDs_are_untouched()
    {
        Assert.Contains(WellKnownSids.Users, Redacted.IdentityOf(Perspective.CurrentUserUnelevated)!.Groups.Select(g => g.Sid));
        Assert.Equal(WellKnownSids.SyntheticStandardUser, Redacted.IdentityOf(Perspective.StandardUser)!.UserSid);
    }

    [Fact]
    public void Host_fields_are_placeholders()
    {
        Assert.Equal(SnapshotRedactor.MachinePlaceholder, Redacted.Host.MachineName);
        Assert.Equal(SnapshotRedactor.UserPlaceholder, Redacted.Host.UserName);
        Assert.Equal(SnapshotRedactor.DomainPlaceholder, Redacted.Host.UserDomain);
        Assert.True(Redacted.Redacted);
    }

    [Fact]
    public void UNC_hosts_are_hidden_but_the_path_shape_stays()
    {
        Assert.Contains(@"\\<host>\share\bin", Redacted.MachinePath.Value);
        Assert.Equal(@"\\<host>\tools", Redacted.Directories[1].DriveTarget);
    }

    [Fact]
    public void Non_identifying_data_is_unchanged()
    {
        var original = Build();

        Assert.Equal(original.MachinePath.Kind, Redacted.MachinePath.Kind);
        Assert.Equal(original.Entries[0].Form, Redacted.Entries[0].Form);
        Assert.Equal(original.Host.OsVersion, Redacted.Host.OsVersion);
        Assert.Equal(original.CapturedAt, Redacted.CapturedAt);
        Assert.Equal(@"C:\Windows\system32", Redacted.EffectivePath![..19]);
    }

    [Fact]
    public void An_account_named_like_an_enum_value_does_not_corrupt_the_snapshot()
    {
        // "User" and "System" are both enum names in the snapshot (PathScope.User, Perspective.System).
        var snapshot = Build() with { Host = Build().Host with { UserName = "User", MachineName = "System" } };

        var redacted = SnapshotRedactor.Redact(snapshot);

        Assert.Equal(PathScope.User, redacted.UserPath.Scope);
        Assert.Equal(Perspective.StandardUser, redacted.Perspectives[1].Perspective);
    }

    [Fact]
    public void Redacting_twice_changes_nothing()
    {
        var twice = SnapshotRedactor.Redact(Redacted);

        Assert.Equal(Json, PathSnapshotJson.Serialize(twice));
    }
}
