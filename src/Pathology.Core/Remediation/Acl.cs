using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Core.Remediation;

/// <summary>What a fix asks of one folder's permissions.</summary>
public sealed record AclFix
{
    /// <summary>The folder, as the scan captured it (canonical).</summary>
    public required string Folder { get; init; }

    /// <summary>The PATH the folder serves. A machine PATH folder is locked to administrators; a user one to you.</summary>
    public PathScope Scope { get; init; }

    /// <summary>Take write access away from every trustee that isn't administrative (or <see cref="KeepWriteSid"/>).</summary>
    public bool StripWrite { get; init; }

    /// <summary>Make Administrators the owner.</summary>
    public bool ResetOwner { get; init; }

    /// <summary>A trustee that keeps its write access: you, for a folder in your user PATH.</summary>
    public string? KeepWriteSid { get; init; }

    /// <summary>Two fixes for the same folder, as one. A machine PATH folder's stricter lock-down wins.</summary>
    public AclFix Merge(AclFix other)
    {
        var machine = Scope == PathScope.Machine || other.Scope == PathScope.Machine;
        return this with
        {
            Scope = machine ? PathScope.Machine : PathScope.User,
            StripWrite = StripWrite || other.StripWrite,
            ResetOwner = ResetOwner || other.ResetOwner,
            KeepWriteSid = machine ? null : KeepWriteSid ?? other.KeepWriteSid,
        };
    }
}

/// <summary>How a folder's permissions are rewritten.</summary>
public enum AclDesignMode
{
    /// <summary>Turn inheritance off, keeping a copy of what was inherited, then take the write access away.</summary>
    Protect,

    /// <summary>
    /// Leave inheritance on and change only the folder's own entries (and owner): a folder above it in the same
    /// plan is being locked down, and that reaches this one through inheritance.
    /// </summary>
    OwnEntriesOnly,

    /// <summary>
    /// Nothing of its own to change: the lock-down of a folder above it reaches it through inheritance. Designed
    /// only to show, and project, what it ends up with.
    /// </summary>
    InheritedOnly,
}

/// <summary>A designed permission change for one folder.</summary>
public sealed record AclDesign
{
    /// <summary>The folder's security descriptor afterwards, as SDDL (what the projection evaluates).</summary>
    public string? Sddl { get; init; }

    /// <summary>The owner afterwards, as a SID.</summary>
    public string? OwnerSid { get; init; }

    /// <summary>What changes, one line per trustee: "Authenticated Users: modify → read &amp; execute".</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>The same change as <c>icacls</c> commands, to read or copy.</summary>
    public IReadOnlyList<string> Commands { get; init; } = [];

    /// <summary>Why it couldn't be designed (an ACL too unusual to rewrite safely), or null.</summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// Designs a lock-down from a folder's captured SDDL. Pure: it parses and rewrites the descriptor and touches
/// nothing. (The parsing lives in <c>Pathology.Windows</c>; Core only says what's wanted.)
/// </summary>
public interface IAclDesigner
{
    AclDesign Design(string path, string sddl, AclFix fix, AclDesignMode mode);
}

/// <summary>Turns the fixes' folder requests into designed changes, one per folder, outermost first.</summary>
public static class AclPlanner
{
    public static IReadOnlyList<AclChange> Plan(PathSnapshot snapshot, IEnumerable<AclFix> fixes, IAclDesigner designer)
    {
        var merged = fixes
            .GroupBy(f => PathText.Key(f.Folder), StringComparer.Ordinal)
            .Select(g => g.Aggregate((a, b) => a.Merge(b)))
            .OrderBy(f => f.Folder.Length)
            .ThenBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changes = new List<AclChange>();
        foreach (var fix in merged)
        {
            var key = PathText.Key(fix.Folder);
            var cover = merged.FirstOrDefault(o => o.StripWrite && !ReferenceEquals(o, fix)
                                                   && DetectionContext.IsUnder(key, PathText.Key(o.Folder)) && key != PathText.Key(o.Folder));
            var facts = snapshot.FactsFor(fix.Folder);
            var mode = cover is null
                ? fix.StripWrite ? AclDesignMode.Protect : AclDesignMode.OwnEntriesOnly
                : fix.ResetOwner || HasOwnWriteGrants(facts) ? AclDesignMode.OwnEntriesOnly : AclDesignMode.InheritedOnly;

            var change = new AclChange
            {
                Path = fix.Folder,
                Scope = fix.Scope,
                Mode = mode,
                CoveredBy = cover?.Folder,
                BeforeSddl = facts?.Sddl ?? "",
                NeedsAdmin = NeedsAdmin(fix, facts),
            };

            if (facts?.Sddl is not { } sddl)
            {
                changes.Add(change with { Refusal = "Its permissions couldn't be read during the scan, so there's nothing to rewrite from." });
                continue;
            }

            var design = designer.Design(fix.Folder, sddl, fix, mode);
            changes.Add(change with
            {
                AfterSddl = design.Sddl,
                AfterOwnerSid = design.OwnerSid,
                Lines = design.Lines,
                Commands = design.Commands,
                Refusal = design.Refusal,
            });
        }
        return changes;
    }

    /// <summary>A non-admin perspective has write access through an entry of the folder's own (not inherited).</summary>
    static bool HasOwnWriteGrants(DirectoryFacts? facts) =>
        facts is not null && new[] { Perspective.StandardUser, Perspective.CurrentUserUnelevated }
            .Select(facts.AccessFor)
            .Any(a => a?.GrantedBy.Any(g => !g.Inherited) == true);

    /// <summary>
    /// A machine PATH folder, or a new owner, always needs an administrator. Your own user PATH folder can be
    /// changed as you when you may already rewrite its permissions.
    /// </summary>
    static bool NeedsAdmin(AclFix fix, DirectoryFacts? facts) =>
        fix.Scope == PathScope.Machine || fix.ResetOwner || facts?.AccessFor(Perspective.CurrentUserUnelevated)?.CanWriteDac != true;
}
