using System.Text.Json.Serialization;

namespace Pathology.Core.Model;

/// <summary>
/// A viewpoint a directory's writability is judged from. The same PATH can be safe from one and exploitable
/// from another, so every probed directory gets an <see cref="AccessResult"/> for each.
/// </summary>
public enum Perspective
{
    /// <summary>This user's normal (UAC-filtered) token: Administrators, if present, is deny-only.</summary>
    CurrentUserUnelevated,

    /// <summary>This user's elevated (linked) token. Equals the unelevated view when UAC is off or the user isn't an admin.</summary>
    CurrentUserElevated,

    /// <summary>LocalSystem, the account services run as: the usual victim of a PATH hijack.</summary>
    System,

    /// <summary>A synthetic, unprivileged interactive user: the usual attacker.</summary>
    StandardUser,

    /// <summary>
    /// This user's unelevated token at Low integrity: what sandboxed code (a browser renderer, a protected-mode
    /// reader, an AppContainer app) starts from. Low integrity can't write anything labelled Medium or above, and an
    /// unlabelled folder counts as Medium, so it can write only folders labelled Low. An AppContainer is Low
    /// integrity with a second check on top, so it can write no more than this.
    /// </summary>
    Sandboxed,
}

/// <summary>The attribute bits a token group carries (<c>SE_GROUP_*</c>).</summary>
[Flags]
public enum GroupAttributes : uint
{
    None = 0,
    Mandatory = 0x1,
    EnabledByDefault = 0x2,
    Enabled = 0x4,
    Owner = 0x8,

    /// <summary>Matches deny ACEs only. How UAC neuters Administrators in a filtered token.</summary>
    DenyOnly = 0x10,

    /// <summary>A mandatory integrity label SID (<c>S-1-16-*</c>).</summary>
    Integrity = 0x20,
    IntegrityEnabled = 0x40,
    Resource = 0x20000000,
    LogonId = 0xC0000000,
}

/// <summary>One SID in a perspective's token, with its attributes.</summary>
public sealed record TokenGroup(string Sid, GroupAttributes Attributes)
{
    /// <summary>True when this SID can satisfy an allow ACE (enabled, not deny-only, not an integrity label).</summary>
    [JsonIgnore]
    public bool MatchesAllowAces =>
        (Attributes & GroupAttributes.Enabled) != 0
        && (Attributes & (GroupAttributes.DenyOnly | GroupAttributes.Integrity)) == 0;
}

/// <summary>The SID set a <see cref="Perspective"/> is evaluated with.</summary>
public sealed record PerspectiveIdentity
{
    public Perspective Perspective { get; init; }

    /// <summary>The token's user SID (synthetic for <see cref="Perspective.StandardUser"/>).</summary>
    public string UserSid { get; init; } = "";

    /// <summary>Every group in the token, including the integrity label and any deny-only groups.</summary>
    public IReadOnlyList<TokenGroup> Groups { get; init; } = [];

    /// <summary>True when the SID set is built from a fixed list rather than read from a real token.</summary>
    public bool Synthetic { get; init; }

    /// <summary>Why this perspective differs from what its name suggests (e.g. "UAC is off"), or null.</summary>
    public string? Note { get; init; }

    /// <summary>The SIDs that satisfy allow ACEs: the user plus every enabled, non-deny-only group.</summary>
    [JsonIgnore]
    public IEnumerable<string> AllowSids =>
        Groups.Where(g => g.MatchesAllowAces).Select(g => g.Sid).Prepend(UserSid);
}
