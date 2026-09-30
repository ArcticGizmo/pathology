using Pathology.Core.Capture;
using Pathology.Core.Model;
using Pathology.Windows.Native;

namespace Pathology.Windows;

/// <summary>
/// Builds the four perspectives' SID sets. The current user's two come from real tokens (this process's and
/// its UAC-linked twin, read at identification level, so no elevation is needed); SYSTEM and the standard
/// user are fixed lists.
/// </summary>
public sealed class TokenPerspectives : ITokenPerspectives
{
    const GroupAttributes On = GroupAttributes.Mandatory | GroupAttributes.EnabledByDefault | GroupAttributes.Enabled;
    const GroupAttributes Label = GroupAttributes.Integrity | GroupAttributes.IntegrityEnabled;

    public IReadOnlyList<PerspectiveIdentity> Build()
    {
        using var token = TokenReader.OpenCurrent();
        var elevation = TokenReader.Elevation(token);
        var current = Read(token);

        PerspectiveIdentity? linked = null;
        string? linkedError = null;
        if (elevation is ElevationType.Full or ElevationType.Limited)
        {
            try
            {
                using var other = TokenReader.Linked(token);
                linked = Read(other);
            }
            catch (Exception ex) { linkedError = ex.Message; }
        }

        PerspectiveIdentity unelevated, elevated;
        switch (elevation)
        {
            case ElevationType.Limited:
                unelevated = current;
                elevated = linked ?? current with { Note = $"The elevated token couldn't be read ({linkedError}); showing the unelevated one." };
                break;
            case ElevationType.Full:
                // PATHology asks for asInvoker, but someone may still run it elevated.
                unelevated = linked ?? current with { Note = $"The filtered token couldn't be read ({linkedError}); showing the elevated one." };
                elevated = current;
                break;
            default:
                unelevated = current;
                elevated = current with { Note = "No split token (UAC is off, or this account isn't an administrator), so elevated is the same as unelevated." };
                break;
        }

        return
        [
            unelevated with { Perspective = Perspective.CurrentUserUnelevated },
            elevated with { Perspective = Perspective.CurrentUserElevated },
            System,
            StandardUser,
        ];
    }

    /// <summary>LocalSystem, the account services run as.</summary>
    public static PerspectiveIdentity System { get; } = new()
    {
        Perspective = Perspective.System,
        UserSid = WellKnownSids.LocalSystem,
        Synthetic = true,
        Groups =
        [
            new(WellKnownSids.Administrators, On | GroupAttributes.Owner),
            new(WellKnownSids.Everyone, On),
            new(WellKnownSids.AuthenticatedUsers, On),
            new(WellKnownSids.SystemIntegrity, Label),
        ],
    };

    /// <summary>
    /// A made-up, unprivileged interactive user: the groups every standard account carries, and a user SID that
    /// matches no real account, so only ACEs granted to everyone like it count.
    /// </summary>
    public static PerspectiveIdentity StandardUser { get; } = new()
    {
        Perspective = Perspective.StandardUser,
        UserSid = WellKnownSids.SyntheticStandardUser,
        Synthetic = true,
        Groups =
        [
            new(WellKnownSids.Everyone, On),
            new(WellKnownSids.Users, On),
            new(WellKnownSids.AuthenticatedUsers, On),
            new(WellKnownSids.Interactive, On),
            new(WellKnownSids.Local, On),
            new(WellKnownSids.MediumIntegrity, Label),
        ],
    };

    static PerspectiveIdentity Read(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token) => new()
    {
        UserSid = TokenReader.User(token),
        Groups = TokenReader.Groups(token),
    };
}
