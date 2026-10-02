using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Core.Detection;

/// <summary>Who could put a file somewhere, from the two attacker perspectives.</summary>
internal enum Attacker
{
    None,

    /// <summary>This user's unelevated token: anything running as you, without a UAC prompt.</summary>
    You,

    /// <summary>The synthetic standard user: any account that can sign in to this PC.</summary>
    AnyUser,
}

/// <summary>
/// The judgements the security detectors share, so "who can plant here" and "what's the root cause" mean the
/// same thing in every rule.
/// </summary>
internal static class Threats
{
    public static Attacker WhoCanPlant(DirectoryFacts? facts, bool countOwnership = true)
    {
        if (facts is not { Exists: true }) return Attacker.None;
        if (Writability.CanPlantFiles(facts.AccessFor(Perspective.StandardUser), countOwnership)) return Attacker.AnyUser;
        if (Writability.CanPlantFiles(facts.AccessFor(Perspective.CurrentUserUnelevated), countOwnership)) return Attacker.You;
        return Attacker.None;
    }

    /// <summary>Who could create the missing folders below <paramref name="ancestor"/>.</summary>
    public static Attacker WhoCanCreateUnder(DirectoryFacts? ancestor)
    {
        if (ancestor is not { Exists: true }) return Attacker.None;
        if (Writability.CanCreateFolders(ancestor.AccessFor(Perspective.StandardUser))) return Attacker.AnyUser;
        if (Writability.CanCreateFolders(ancestor.AccessFor(Perspective.CurrentUserUnelevated))) return Attacker.You;
        return Attacker.None;
    }

    /// <summary>
    /// True when planting in a machine-PATH folder is an escalation to SYSTEM: anyone can, or only you can and
    /// you're a standard user. False when only you can and you're an administrator: a UAC bypass, since you
    /// could elevate anyway. Either is High; the difference is in how the finding explains it.
    /// </summary>
    public static bool IsEscalation(Attacker attacker, DetectionContext context) =>
        attacker == Attacker.AnyUser || !context.UserIsAdmin;

    public static string Who(Attacker attacker) =>
        attacker == Attacker.AnyUser ? "any user on this PC" : "you, without elevating";

    public static IReadOnlyList<Perspective> Perspectives(Attacker attacker, PathScope scope)
    {
        var who = attacker == Attacker.AnyUser ? Perspective.StandardUser : Perspective.CurrentUserUnelevated;
        return scope == PathScope.Machine
            ? [who, Perspective.System, Perspective.CurrentUserElevated]
            : [who, Perspective.CurrentUserUnelevated, Perspective.CurrentUserElevated];
    }

    /// <summary>The ACEs (or ownership) behind the attacker's access, one line each.</summary>
    public static IEnumerable<string> AccessEvidence(DirectoryFacts facts, Attacker attacker, DetectionContext context) => attacker switch
    {
        Attacker.AnyUser => Writability.EvidenceFor(facts.AccessFor(Perspective.StandardUser), "Any user", context.Snapshot.Host.UserSid),
        Attacker.You => Writability.EvidenceFor(facts.AccessFor(Perspective.CurrentUserUnelevated), "You (unelevated)", context.Snapshot.Host.UserSid),
        _ => [],
    };

    /// <summary>
    /// Where a folder's write access for every user comes from, when it's all inherited from a well-known
    /// source: the drive root (a folder made directly under <c>C:\</c> inherits Authenticated Users' modify
    /// right) or <c>ProgramData</c> (which lets Users write into the subfolders it hands down). Null otherwise.
    /// </summary>
    public static (string Key, string Display)? InheritedFrom(DetectionContext context, DirectoryFacts facts)
    {
        var access = facts.AccessFor(Perspective.StandardUser);
        if (!Writability.CanPlantFiles(access, countOwnership: false)) return null;
        if (access!.GrantedBy.Count == 0 || access.GrantedBy.Any(a => !a.Inherited)) return null;
        if (PathText.Classify(facts.Path) != PathForm.Absolute || facts.Drive is not DriveKind.Fixed) return null;

        var key = PathText.Key(facts.Path);
        if (context.Variable("ProgramData") is { } programData && DetectionContext.KeyOf(programData) is var dataKey
            && key != dataKey && DetectionContext.IsUnder(key, dataKey))
            return (dataKey, PathText.Canonical(PathText.Strip(programData)) ?? programData);

        // Inside Windows, Program Files or the profiles, the inheritance starts somewhere within that tree.
        string[] protectedTrees =
        [
            context.SystemRootKey, context.ProfilesRootKey,
            .. new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" }.Select(context.Variable).OfType<string>().Select(DetectionContext.KeyOf),
        ];
        if (protectedTrees.Any(t => DetectionContext.IsUnder(key, t))) return null;

        var root = PathText.RootOf(facts.Path)!;
        return (PathText.Key(root), root);
    }

    /// <summary>
    /// The root cause of a folder being writable: the inheritance source when there is one (one fix for every
    /// folder it covers), otherwise the folder's own ACL.
    /// </summary>
    public static string WritableRootCause(DetectionContext context, DirectoryFacts facts) =>
        InheritedFrom(context, facts) is { } source ? "inherited:" + source.Key : "acl:" + PathText.Key(facts.Path);

    public static string MissingRootCause(DirectoryFacts facts) => "missing:" + PathText.Key(facts.Path);
}
