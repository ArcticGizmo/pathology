using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Core.Remediation;

/// <summary>Where a draft entry came from: an entry of the scanned PATH.</summary>
public sealed record EntryOrigin(PathScope Scope, int Index);

/// <summary>One entry of a proposed PATH value.</summary>
/// <param name="Id">
/// Stable for the draft's lifetime: an entry keeps its id through every edit, so edits name entries by id and
/// replay cleanly when a suggestion is toggled after a manual edit.
/// </param>
/// <param name="Text">The entry as it will be stored.</param>
/// <param name="Origin">The scanned entry it started as, or null for one that was added.</param>
public sealed record DraftEntry(int Id, string Text, EntryOrigin? Origin);

/// <summary>
/// Both PATH values as they would be stored after a set of edits: entries, value kinds, and whether each value
/// keeps the trailing <c>;</c> Windows writes. Immutable; every edit returns a new draft.
/// </summary>
public sealed record PathDraft
{
    /// <summary>Ids the planner gives the entries its suggestions add.</summary>
    public const int SuggestionIdBase = 1_000_000;

    /// <summary>Ids the editor gives the entries you add.</summary>
    public const int ManualIdBase = 2_000_000;

    public IReadOnlyList<DraftEntry> Machine { get; init; } = [];
    public IReadOnlyList<DraftEntry> User { get; init; } = [];
    public PathValueKind MachineKind { get; init; } = PathValueKind.Missing;
    public PathValueKind UserKind { get; init; } = PathValueKind.Missing;
    public bool MachineTrailingSeparator { get; init; }
    public bool UserTrailingSeparator { get; init; }

    /// <summary>
    /// The PATH as scanned. The empty slot a trailing <c>;</c> leaves isn't an entry: it's remembered as
    /// <see cref="MachineTrailingSeparator"/> / <see cref="UserTrailingSeparator"/> and written back as it was.
    /// </summary>
    public static PathDraft From(PathSnapshot snapshot)
    {
        var id = 0;
        List<DraftEntry> Of(PathScope scope) => snapshot.EntriesIn(scope)
            .Where(e => !e.Defects.HasFlag(HygieneDefects.TrailingSeparator))
            .Select(e => new DraftEntry(id++, e.Raw, new EntryOrigin(scope, e.Index)))
            .ToList();

        bool Trailing(PathScope scope) => snapshot.EntriesIn(scope).Any(e => e.Defects.HasFlag(HygieneDefects.TrailingSeparator));

        return new PathDraft
        {
            Machine = Of(PathScope.Machine),
            User = Of(PathScope.User),
            MachineKind = snapshot.MachinePath.Kind,
            UserKind = snapshot.UserPath.Kind,
            MachineTrailingSeparator = Trailing(PathScope.Machine),
            UserTrailingSeparator = Trailing(PathScope.User),
        };
    }

    public IReadOnlyList<DraftEntry> EntriesIn(PathScope scope) => scope == PathScope.Machine ? Machine : User;

    public PathValueKind KindOf(PathScope scope) => scope == PathScope.Machine ? MachineKind : UserKind;

    /// <summary>All entries in search order: machine, then user.</summary>
    public IEnumerable<DraftEntry> All => Machine.Concat(User);

    /// <summary>The scope and position of an entry, or null when the draft doesn't have it.</summary>
    public (PathScope Scope, int Index)? Locate(int id)
    {
        foreach (var scope in new[] { PathScope.Machine, PathScope.User })
        {
            var list = EntriesIn(scope);
            for (var i = 0; i < list.Count; i++)
                if (list[i].Id == id) return (scope, i);
        }
        return null;
    }

    public DraftEntry? Find(int id) => Locate(id) is { } at ? EntriesIn(at.Scope)[at.Index] : null;

    /// <summary>The value as it would be stored: the entries joined with <c>;</c>, or null for a value that stays missing.</summary>
    public string? ValueOf(PathScope scope)
    {
        var kind = KindOf(scope);
        var entries = EntriesIn(scope);
        if (kind == PathValueKind.Missing) return null;
        if (entries.Count == 0) return "";
        var trailing = scope == PathScope.Machine ? MachineTrailingSeparator : UserTrailingSeparator;
        return string.Join(';', entries.Select(e => e.Text)) + (trailing ? ";" : "");
    }

    public RawPathValue RawOf(PathScope scope) => KindOf(scope) == PathValueKind.Missing
        ? RawPathValue.Missing(scope)
        : new RawPathValue { Scope = scope, Value = ValueOf(scope), Kind = KindOf(scope) };

    public PathDraft Apply(IEnumerable<EntryEdit> edits) => edits.Aggregate(this, (draft, edit) => draft.Apply(edit));

    /// <summary>Apply one edit. An edit naming an entry the draft no longer has does nothing.</summary>
    public PathDraft Apply(EntryEdit edit) => edit switch
    {
        RemoveEntry e => Locate(e.Id) is { } at ? With(at.Scope, EntriesIn(at.Scope).Where(x => x.Id != e.Id).ToList()) : this,

        ReplaceText e => Locate(e.Id) is { } at
            ? With(at.Scope, EntriesIn(at.Scope).Select(x => x.Id == e.Id ? x with { Text = e.Text } : x).ToList())
            : this,

        TidyText e => Locate(e.Id) is { } at
            ? With(at.Scope, EntriesIn(at.Scope).Select(x => x.Id == e.Id ? x with { Text = EntryText.Tidy(x.Text, e.Defects) } : x).ToList())
            : this,

        MoveEntry e => Move(e),

        MoveToUser e => Locate(e.Id) is { Scope: PathScope.Machine } at ? ToUserFront(EntriesIn(PathScope.Machine)[at.Index]) : this,

        AddEntry e when Find(e.Id) is null => Insert(e.Scope, e.Index, new DraftEntry(e.Id, e.Text, null)),
        AddEntry => this,

        SetKind e => e.Scope == PathScope.Machine ? this with { MachineKind = e.Kind } : this with { UserKind = e.Kind },

        Reorder e => ReorderScope(e),

        _ => throw new ArgumentOutOfRangeException(nameof(edit), edit, "Unknown edit"),
    };

    PathDraft Move(MoveEntry e)
    {
        if (Locate(e.Id) is not { } at) return this;
        var entry = EntriesIn(at.Scope)[at.Index];
        var removed = With(at.Scope, EntriesIn(at.Scope).Where(x => x.Id != e.Id).ToList());
        return removed.Insert(e.Scope, e.Index, entry);
    }

    /// <summary>
    /// To the front of the user PATH, after any entries already moved there that came earlier in the machine PATH, so
    /// entries moved one by one keep the order they were searched in.
    /// </summary>
    PathDraft ToUserFront(DraftEntry entry)
    {
        static int MachineOrder(DraftEntry e) => e.Origin is { Scope: PathScope.Machine } o ? o.Index : int.MaxValue;
        var removed = With(PathScope.Machine, Machine.Where(x => x.Id != entry.Id).ToList());
        var index = removed.User.TakeWhile(u => MachineOrder(u) < MachineOrder(entry)).Count();
        return removed.Insert(PathScope.User, index, entry);
    }

    PathDraft Insert(PathScope scope, int index, DraftEntry entry)
    {
        var list = EntriesIn(scope).ToList();
        list.Insert(Math.Clamp(index, 0, list.Count), entry);
        var draft = With(scope, list);
        // A value that didn't exist is created as REG_EXPAND_SZ, the kind Windows itself uses for PATH.
        return draft.KindOf(scope) == PathValueKind.Missing ? draft.Apply(new SetKind(scope, PathValueKind.ExpandString)) : draft;
    }

    PathDraft ReorderScope(Reorder e)
    {
        var list = EntriesIn(e.Scope);
        var first = e.Ids.Select(id => list.FirstOrDefault(x => x.Id == id)).OfType<DraftEntry>().Distinct().ToList();
        return With(e.Scope, [.. first, .. list.Where(x => !first.Contains(x))]);
    }

    PathDraft With(PathScope scope, IReadOnlyList<DraftEntry> entries) =>
        scope == PathScope.Machine ? this with { Machine = entries } : this with { User = entries };
}

/// <summary>One change to a <see cref="PathDraft"/>. Entries are named by id, never by position.</summary>
public abstract record EntryEdit;

public sealed record RemoveEntry(int Id) : EntryEdit;

public sealed record ReplaceText(int Id, string Text) : EntryEdit;

/// <summary>Clean the given defects out of an entry's text, whatever its text is by then.</summary>
public sealed record TidyText(int Id, HygieneDefects Defects) : EntryEdit;

/// <summary>Move an entry to <paramref name="Index"/> in <paramref name="Scope"/> (counted once it's taken out; clamped).</summary>
public sealed record MoveEntry(int Id, PathScope Scope, int Index) : EntryEdit;

/// <summary>
/// Move a machine entry to the front of the user PATH, keeping the machine order among entries moved the same way:
/// it was searched before every user entry, and this keeps it as close to that as the user PATH allows.
/// </summary>
public sealed record MoveToUser(int Id) : EntryEdit;

/// <summary>Add an entry. The id comes from <see cref="PathDraft.SuggestionIdBase"/> or <see cref="PathDraft.ManualIdBase"/>.</summary>
public sealed record AddEntry(int Id, PathScope Scope, int Index, string Text) : EntryEdit;

public sealed record SetKind(PathScope Scope, PathValueKind Kind) : EntryEdit;

/// <summary>Put these entries first in their scope, in this order; the rest keep their order after them.</summary>
public sealed record Reorder(PathScope Scope, IReadOnlyList<int> Ids) : EntryEdit;

/// <summary>Cleaning up an entry's text, defect by defect.</summary>
public static class EntryText
{
    /// <summary>The hygiene defects <see cref="Tidy"/> knows how to clean.</summary>
    public const HygieneDefects Tidyable =
        HygieneDefects.Quotes | HygieneDefects.LeadingWhitespace | HygieneDefects.TrailingWhitespace
        | HygieneDefects.DoubledBackslash | HygieneDefects.ForwardSlash;

    /// <summary>
    /// Remove quotes (and the spaces inside them), trim edge spaces, turn <c>/</c> into <c>\</c> and collapse doubled backslashes (keeping a
    /// leading <c>\\</c>, which is a UNC or device prefix), as asked. A trailing backslash is left alone.
    /// </summary>
    public static string Tidy(string text, HygieneDefects defects)
    {
        var s = text;
        // Spaces that sat inside the quotes are part of the same mistake.
        if (defects.HasFlag(HygieneDefects.Quotes)) s = s.Replace("\"", "").Trim();
        if (defects.HasFlag(HygieneDefects.LeadingWhitespace)) s = s.TrimStart();
        if (defects.HasFlag(HygieneDefects.TrailingWhitespace)) s = s.TrimEnd();
        if (defects.HasFlag(HygieneDefects.ForwardSlash)) s = s.Replace('/', '\\');
        if (defects.HasFlag(HygieneDefects.DoubledBackslash))
        {
            var lead = s.Length - s.TrimStart().Length;
            var prefix = s.AsSpan(lead).StartsWith(@"\\") ? lead + 2 : 0;
            var rest = s[prefix..];
            while (rest.Contains(@"\\", StringComparison.Ordinal)) rest = rest.Replace(@"\\", @"\");
            s = s[..prefix] + rest;
        }
        return s;
    }

    /// <summary>Every tidyable defect the text has.</summary>
    public static HygieneDefects DefectsOf(string text) => PathTokeniser.Inspect(text) & Tidyable;
}
