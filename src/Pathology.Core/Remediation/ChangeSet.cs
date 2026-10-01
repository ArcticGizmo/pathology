using System.Globalization;
using System.Text.Json.Serialization;
using Pathology.Core.Detection.Detectors;
using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Core.Remediation;

/// <summary>One PATH value, as it is and as it will be.</summary>
public sealed record ValueChange
{
    public required RawPathValue Before { get; init; }
    public required RawPathValue After { get; init; }

    [JsonIgnore] public PathScope Scope => After.Scope;

    /// <summary>The machine PATH lives in <c>HKLM</c>, so changing it takes an administrator.</summary>
    [JsonIgnore] public bool NeedsAdmin => Scope == PathScope.Machine;
}

/// <summary>One folder's permissions, as they are and as they will be.</summary>
public sealed record AclChange
{
    public required string Path { get; init; }
    public PathScope Scope { get; init; }
    public AclDesignMode Mode { get; init; }

    /// <summary>The enclosing folder whose lock-down reaches this one through inheritance, or null.</summary>
    public string? CoveredBy { get; init; }

    /// <summary>The SDDL the scan captured: what has to be there still when the change is applied.</summary>
    public string BeforeSddl { get; init; } = "";

    public string? AfterSddl { get; init; }
    public string? AfterOwnerSid { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
    public IReadOnlyList<string> Commands { get; init; } = [];
    public bool NeedsAdmin { get; init; }

    /// <summary>Why this folder can't be changed, or null.</summary>
    public string? Refusal { get; init; }

    /// <summary>Something is actually written to this folder (an inherited-only change is the enclosing folder's write).</summary>
    [JsonIgnore]
    public bool Writes => Refusal is null && Mode != AclDesignMode.InheritedOnly && AfterSddl is not null && Lines.Count > 0;
}

/// <summary>
/// Every write an apply would make: each PATH value that changes and each folder whose permissions do. Built
/// purely from a snapshot and a plan; nothing is written until <see cref="ChangeApplier"/> is asked to.
/// </summary>
public sealed record ChangeSet
{
    /// <summary>The longest value Windows can hold in an environment variable.</summary>
    public const int MaxValueLength = LengthHeadroom.VariableLimit;

    public Guid Id { get; init; } = Guid.NewGuid();

    public IReadOnlyList<ValueChange> Values { get; init; } = [];

    public IReadOnlyList<AclChange> Acls { get; init; } = [];

    /// <summary>Nothing would be written.</summary>
    [JsonIgnore] public bool IsEmpty => Values.Count == 0 && !Acls.Any(a => a.Writes);

    /// <summary>Applying needs one UAC prompt.</summary>
    [JsonIgnore] public bool NeedsAdmin => Values.Any(v => v.NeedsAdmin) || Acls.Any(a => a.Writes && a.NeedsAdmin);

    public ValueChange? ValueFor(PathScope scope) => Values.FirstOrDefault(v => v.Scope == scope);

    /// <summary>The value each scope will have: the change's after, or the snapshot's value.</summary>
    public RawPathValue Resulting(PathSnapshot snapshot, PathScope scope) => ValueFor(scope)?.After ?? snapshot.PathFor(scope);

    /// <summary>
    /// The user PATH to write before the machine PATH, or null when the order doesn't matter. An entry moving from the
    /// machine PATH to the user PATH can't move in one write: they're separate values, and only the elevated helper
    /// writes the machine one. So the user PATH gets its copy first and the machine PATH lets it go after, and an
    /// apply that stops in between leaves the entry in both, never in neither. An entry going the other way stays in
    /// this first user value too, and leaves it only once the machine PATH has it.
    /// </summary>
    public RawPathValue? UserFirst()
    {
        if (ValueFor(PathScope.Machine) is not { } machine || ValueFor(PathScope.User) is not { } user) return null;
        var leavingMachine = Keys(machine.Before).Except(Keys(machine.After));
        if (!Keys(user.After).Except(Keys(user.Before)).Intersect(leavingMachine).Any()) return null;

        var goingUp = Keys(user.Before).Except(Keys(user.After)).Intersect(Keys(machine.After).Except(Keys(machine.Before))).ToHashSet();
        var stay = EntriesOf(user.Before).Where(e => goingUp.Contains(KeyOf(e))).ToList();
        if (stay.Count == 0) return user.After;
        var trailing = user.After.Value?.EndsWith(';') == true;
        return user.After with { Value = string.Join(';', EntriesOf(user.After).Concat(stay)) + (trailing ? ";" : "") };
    }

    /// <summary>A value's entries, without the empty slot a trailing <c>;</c> leaves.</summary>
    static IEnumerable<string> EntriesOf(RawPathValue value) =>
        PathTokeniser.Split(value.Value).Where(s => !s.Defects.HasFlag(HygieneDefects.TrailingSeparator)).Select(s => s.Text);

    static string KeyOf(string entry) => PathText.Key(PathText.Strip(entry));

    static HashSet<string> Keys(RawPathValue value) =>
        EntriesOf(value).Where(e => PathText.Strip(e).Length > 0).Select(KeyOf).ToHashSet(StringComparer.Ordinal);

    /// <summary>The changes a draft and a set of folder designs add up to, against what the scan saw.</summary>
    public static ChangeSet From(PathSnapshot snapshot, PathDraft draft, IReadOnlyList<AclChange>? acls = null)
    {
        var values = new List<ValueChange>();
        foreach (var scope in new[] { PathScope.Machine, PathScope.User })
        {
            var before = snapshot.PathFor(scope);
            var after = draft.RawOf(scope);
            if (before.Kind == after.Kind && string.Equals(before.Value, after.Value, StringComparison.Ordinal)) continue;
            values.Add(new ValueChange { Before = before, After = after });
        }
        return new ChangeSet { Values = values, Acls = acls ?? [] };
    }

    /// <summary>Reasons the set mustn't be applied as it stands (empty when it may be).</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var v in Values)
        {
            var name = v.Scope == PathScope.Machine ? "The machine PATH" : "Your user PATH";
            if (v.After.Length > MaxValueLength)
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name} would be {v.After.Length:N0} characters, more than the {MaxValueLength:N0} Windows can hold."));
            if (v.Before.Kind == PathValueKind.Other)
                problems.Add($"{name} isn't stored as a string, so PATHology won't rewrite it.");
            if (v.After.Kind is not (PathValueKind.String or PathValueKind.ExpandString or PathValueKind.Missing))
                problems.Add($"{name} would be written with a kind that isn't REG_SZ or REG_EXPAND_SZ.");
        }
        if (UserFirst() is { Length: > MaxValueLength } first)
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"Your user PATH would be {first.Length:N0} characters while entries move between the two, more than the {MaxValueLength:N0} Windows can hold."));
        return problems;
    }
}
