using Pathology.Core.Model;

namespace Pathology.Tests.Capture;

/// <summary>
/// A hand-built snapshot with made-up identifying values planted in every kind of field, so the JSON and
/// redaction tests can check they survive (or don't) wherever they appear. None of it is this machine's.
/// </summary>
internal static class SampleSnapshot
{
    public const string User = "alex.example";
    public const string Machine = "WS-EXAMPLE-42";
    public const string Domain = "EXAMPLECORP";
    public const string DomainSid = "S-1-5-21-1111111111-2222222222-3333333333";
    public const string UserSid = DomainSid + "-1104";
    public const string OtherUserSid = DomainSid + "-1177";
    public const string Profile = @"C:\Users\alex.example";
    public const string AzureOwner = "S-1-12-1-555555555-666666666-777777777-888888888";

    public static PathSnapshot Build() => new()
    {
        CapturedAt = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
        Host = new HostInfo
        {
            MachineName = Machine, UserName = User, UserDomain = Domain, UserSid = UserSid, UserProfile = Profile,
            OsVersion = "10.0.26200.1000", Elevation = ElevationType.Limited, EnableLua = true,
        },
        MachinePath = new RawPathValue { Scope = PathScope.Machine, Kind = PathValueKind.ExpandString, Value = @"%SystemRoot%\system32;C:\Users\alex.example\tools;\\fileserver01\share\bin" },
        UserPath = new RawPathValue { Scope = PathScope.User, Kind = PathValueKind.ExpandString, Value = @"%USERPROFILE%\bin;C:\Users\ALEXEX~1\AppData\Local\x;C:\Users\sam.other\bin;C:\Users\alex.example\OneDrive - Example Corp\bin" },
        Environment =
        [
            new EnvironmentVariables
            {
                Source = EnvironmentSource.NewProcess,
                Variables = new Dictionary<string, string?>
                {
                    ["USERPROFILE"] = Profile,
                    ["USERNAME"] = User,
                    ["COMPUTERNAME"] = Machine,
                    ["USERDOMAIN"] = Domain,
                    ["LOGONSERVER"] = @"\\DC-EXAMPLE-01",
                    ["UPN"] = "alex.example@example.com",
                    ["PUBLIC"] = @"C:\Users\Public",
                },
            },
        ],
        EffectivePath = @"C:\Windows\system32;C:\Users\alex.example\tools",
        Perspectives =
        [
            new PerspectiveIdentity
            {
                Perspective = Perspective.CurrentUserUnelevated,
                UserSid = UserSid,
                Groups = [new(WellKnownSids.Users, GroupAttributes.Enabled), new(DomainSid + "-513", GroupAttributes.Enabled), new("S-1-5-5-0-123456", GroupAttributes.Enabled | GroupAttributes.LogonId)],
            },
            new PerspectiveIdentity { Perspective = Perspective.StandardUser, UserSid = WellKnownSids.SyntheticStandardUser, Synthetic = true },
        ],
        Entries =
        [
            new PathEntry { Scope = PathScope.User, Index = 0, Raw = @"%USERPROFILE%\bin", Expanded = @"C:\Users\alex.example\bin", Form = PathForm.Absolute, ProbePath = @"C:\Users\alex.example\bin", Key = @"C:\USERS\ALEX.EXAMPLE\BIN" },
        ],
        Directories =
        [
            new DirectoryFacts
            {
                Path = @"C:\Users\alex.example\bin", Status = ProbeStatus.Probed, Exists = true, IsDirectory = true, Drive = DriveKind.Fixed,
                OwnerSid = UserSid,
                Sddl = $"O:{UserSid}G:{DomainSid}-513D:(A;OICI;FA;;;{UserSid})(A;OICI;FA;;;SY)(A;;0x1200a9;;;S-1-12-1-111-222-333-444)",
                Access = [new AccessResult { Perspective = Perspective.CurrentUserUnelevated, Granted = FileAccessRights.AddFile, OwnerInPerspective = true, GrantedBy = [new GrantingAce { Sid = UserSid, Mask = FileAccessRights.AddFile }] }],
            },
            new DirectoryFacts { Path = @"Z:\tools", Drive = DriveKind.MappedNetwork, DriveTarget = @"\\fileserver01\tools", Status = ProbeStatus.SkippedNetwork },
            // An Azure AD owner: in SDDL its SID runs straight into "G:", with no word boundary after it.
            new DirectoryFacts { Path = @"C:\Tools", OwnerSid = AzureOwner, Sddl = $"O:{AzureOwner}G:{AzureOwner}D:(A;;FA;;;{AzureOwner})" },
        ],
    };
}
