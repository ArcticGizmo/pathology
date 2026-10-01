using Pathology.Core.Detection;
using Pathology.Core.Detection.Detectors;
using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Core.Remediation;

/// <summary>
/// One fix PATHology can make for a problem: edits to the PATH values and/or folder lock-downs. Ticking it
/// applies all of it; nothing is written until the change set it adds up to is applied.
/// </summary>
public sealed record SuggestedFix
{
    /// <summary>Stable across scans (it's built from the problem's root cause), so a choice survives a re-scan.</summary>
    public required string Id { get; init; }

    /// <summary>What it does, as an action: "Remove C:\Old\bin from the machine PATH".</summary>
    public required string Title { get; init; }

    /// <summary>The problem it fixes, in the finding's words.</summary>
    public required string Problem { get; init; }

    /// <summary>A side effect worth knowing before ticking it, or null.</summary>
    public string? Note { get; init; }

    public FindingCategory Category { get; init; }
    public Severity Severity { get; init; }

    public IReadOnlyList<EntryEdit> Edits { get; init; } = [];
    public IReadOnlyList<AclFix> Acls { get; init; } = [];

    /// <summary>The root causes (problems) it's meant to fix.</summary>
    public IReadOnlyList<string> RootCauses { get; init; } = [];

    /// <summary>Ticked to begin with. Fixes that change which tool runs, or remove something you may still use, aren't.</summary>
    public bool Recommended { get; init; } = true;

    /// <summary>Applying it takes a UAC prompt (a machine PATH edit, or any folder lock-down that needs an administrator).</summary>
    public bool NeedsAdmin { get; init; }
}

/// <summary>
/// Works out a fix for each problem it can fix. Pure: the same diagnosis always gives the same suggestions, and
/// they're only suggestions until a change set built from them is applied.
/// </summary>
public static class RemediationPlanner
{
    public const string WindowsFirstId = "order:windows-first";

    public static IReadOnlyList<SuggestedFix> Suggest(Diagnosis diagnosis, DetectionContext context)
    {
        var snapshot = diagnosis.Snapshot;
        var draft = PathDraft.From(snapshot);
        var ids = draft.All.Where(e => e.Origin is not null).ToDictionary(e => e.Origin!, e => e.Id);

        var fixes = new List<SuggestedFix>();
        foreach (var group in diagnosis.Groups.Where(g => g.Severity > Severity.Info))
            if (ForGroup(group, context, ids) is { } fix)
                fixes.Add(fix with
                {
                    Id = "fix:" + group.RootCause,
                    Problem = group.Primary.Title,
                    Category = group.Primary.Category,
                    Severity = group.Severity,
                    RootCauses = [group.RootCause],
                    NeedsAdmin = NeedsAdmin(fix, context),
                });

        if (WindowsFirst(diagnosis, context, draft) is { } order) fixes.Add(order);
        return fixes;
    }

    static SuggestedFix? ForGroup(FindingGroup group, DetectionContext context, Dictionary<EntryOrigin, int> ids)
    {
        var rc = group.RootCause;
        var kind = rc.Contains(':') ? rc[..rc.IndexOf(':')] : rc;
        var entries = group.Members.SelectMany(f => f.Entries)
            .DistinctBy(e => (e.Scope, e.Index))
            .Where(e => ids.ContainsKey(new EntryOrigin(e.Scope, e.Index)))
            .ToList();
        int Id(EntryRef e) => ids[new EntryOrigin(e.Scope, e.Index)];
        var rules = group.Members.Select(f => f.Rule).ToHashSet(StringComparer.Ordinal);

        switch (kind)
        {
            case "missing" when entries.Count > 0:
                return Fix(
                    title: $"Remove {group.Primary.Subject} from {Where(entries)}",
                    edits: entries.Select(e => (EntryEdit)new RemoveEntry(Id(e))),
                    // An unmounted drive letter may just be a USB drive that's out.
                    recommended: !rules.Contains("SEC-09"));

            case "location" when entries.Count > 0:
                return Fix(
                    title: $"Remove {group.Primary.Subject} from {Where(entries)}",
                    edits: entries.Select(e => (EntryEdit)new RemoveEntry(Id(e))),
                    note: "Only if you don't need what's there: the tools in it stop being found by name.",
                    recommended: false);

            case "entry" when entries.Count == 1:
                return ForEntry(entries[0], Id(entries[0]), rules, context);

            case "empty" when entries.Count > 0:
                return Fix(
                    title: $"Remove the empty {(entries.Count == 1 ? "entry" : "entries")} from {Where(entries)}",
                    edits: entries.Select(e => (EntryEdit)new RemoveEntry(Id(e))));

            case "dup" when entries.Count > 1:
            {
                var extra = entries.Skip(1).ToList();
                return Fix(
                    title: $"Remove the {(extra.Count == 1 ? "extra copy" : $"{extra.Count} extra copies")} of {entries[0].Display}",
                    edits: extra.Select(e => (EntryEdit)new RemoveEntry(Id(e))));
            }

            case "kind" when ScopeOf(rc) is { } scope:
                return Fix(
                    title: $"Store {Words.Path(scope)} as REG_EXPAND_SZ, so its %variables% expand",
                    edits: [new SetKind(scope, PathValueKind.ExpandString)]);

            case "no-system32":
                return RestoreWindows(context);

            case "HYG-01" or "HYG-02" or "HYG-03" when entries.Count > 0:
            {
                var defects = kind switch
                {
                    "HYG-01" => HygieneDefects.Quotes,
                    "HYG-02" => HygieneDefects.LeadingWhitespace | HygieneDefects.TrailingWhitespace,
                    _ => HygieneDefects.DoubledBackslash | HygieneDefects.ForwardSlash,
                };
                var what = kind switch
                {
                    "HYG-01" => "Remove the quotes from",
                    "HYG-02" => "Trim the spaces from",
                    _ => "Use single backslashes in",
                };
                return Fix(
                    title: $"{what} {Words.Count(entries.Count, "entry", "entries")} in {Where(entries)}",
                    edits: entries.Select(e => (EntryEdit)new TidyText(Id(e), defects)));
            }

            case "inherited" or "acl":
                return LockDown(group, context);

            case "owner" when FolderOf(group.Primary, context) is { } folder:
                return Fix(
                    title: $"Make Administrators the owner of {folder}",
                    acls: [new AclFix { Folder = folder, Scope = PathScope.Machine, ResetOwner = true }]);

            default:
                return null;
        }
    }

    /// <summary>COR-01, COR-03 and COR-04, which are about one entry: move it where it works, or remove it.</summary>
    static SuggestedFix? ForEntry(EntryRef entry, int id, HashSet<string> rules, DetectionContext context)
    {
        var scanned = context.Snapshot.EntriesIn(entry.Scope).FirstOrDefault(e => e.Index == entry.Index);
        if (scanned is null) return null;

        var userOnly = entry.Scope == PathScope.Machine && rules.Contains("COR-01") && scanned.UnresolvedVariables.Any(v =>
            context.Defines(EnvironmentSource.User, v) && !context.Defines(EnvironmentSource.Machine, v) && !context.Defines(EnvironmentSource.Volatile, v));
        var yourProfile = rules.Contains("COR-04")
                          && (scanned.Variables.Count > 0 || context.UserProfileKey is { } mine && DetectionContext.IsUnder(scanned.Key, mine));

        if (entry.Scope == PathScope.Machine && (userOnly || yourProfile))
        {
            // Where your user PATH already has the folder, moving it would only make a duplicate.
            var asUser = DetectionContext.KeyOf(EnvironmentExpander.Expand(scanned.Raw, context.Variable).Text);
            if (context.Snapshot.EntriesIn(PathScope.User).FirstOrDefault(e => e.Key == asUser) is { } already)
                return Fix(
                    title: $"Remove {Words.Display(scanned)} from the machine PATH",
                    edits: [new RemoveEntry(id)],
                    note: $"Your user PATH already has {Words.Display(already)}, where it works. Other accounts stop seeing it.");

            return Fix(
                title: $"Move {Words.Display(scanned)} to your user PATH",
                edits: [new MoveEntry(id, PathScope.User, 0)],
                note: "It goes first in your user PATH, so it's still searched before your other entries. Other accounts stop seeing it.");
        }

        if (rules.Contains("COR-04"))
            return Fix(
                title: $"Remove {Words.Display(scanned)} from the machine PATH",
                edits: [new RemoveEntry(id)],
                note: "It's inside another account's profile. If that account needs it, add it to that account's own PATH.",
                recommended: false);

        return Fix(
            title: $"Remove \"{scanned.Raw.Trim()}\" from {Words.Path(entry.Scope)}",
            edits: [new RemoveEntry(id)]);
    }

    /// <summary>COR-09: put Windows' stock entries back at the start of the machine PATH, expandable.</summary>
    static SuggestedFix RestoreWindows(DetectionContext context)
    {
        var stock = WindowsFoldersMissing.StockEntries.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var edits = new List<EntryEdit> { new SetKind(PathScope.Machine, PathValueKind.ExpandString) };
        edits.AddRange(stock.Select((text, i) => (EntryEdit)new AddEntry(PathDraft.SuggestionIdBase + i, PathScope.Machine, i, text)));
        return Fix(
            title: "Put Windows' own folders back at the start of the machine PATH",
            edits: edits,
            note: string.Join("; ", stock) + ". Stored as REG_EXPAND_SZ.");
    }

    /// <summary>SEC-01/04/05/06/08: lock down the folders someone else can write.</summary>
    static SuggestedFix? LockDown(FindingGroup group, DetectionContext context)
    {
        var userSid = context.Snapshot.Host.UserSid;
        var folders = new Dictionary<string, AclFix>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in group.Members)
            foreach (var entry in finding.Entries)
            {
                if (context.Snapshot.EntriesIn(entry.Scope).FirstOrDefault(e => e.Index == entry.Index) is not { } scanned) continue;
                if (context.Resolve(scanned).Final is not { Exists: true, IsDirectory: true } folder) continue;
                var fix = new AclFix
                {
                    Folder = folder.Path,
                    Scope = entry.Scope,
                    StripWrite = true,
                    KeepWriteSid = entry.Scope == PathScope.User ? userSid : null,
                };
                folders[folder.Path] = folders.TryGetValue(folder.Path, out var known) ? known.Merge(fix) : fix;
            }
        if (folders.Count == 0) return null;

        var machine = folders.Values.Any(f => f.Scope == PathScope.Machine);
        var title = folders.Count == 1
            ? $"Lock down {folders.Keys.First()}"
            : group.RootCause.StartsWith("inherited:", StringComparison.Ordinal)
                ? $"Lock down the {folders.Count} folders that inherit write access from {group.Primary.Subject}"
                : $"Lock down {Words.List(folders.Keys)}";
        return Fix(
            title: title,
            acls: folders.Values,
            note: machine
                ? "Only administrators will be able to add files, so installing into these folders (pip install, npm -g, an updater) will need an elevated prompt."
                : "Only you and administrators will be able to add files.");
    }

    /// <summary>Anything other than Windows' own folders ahead of System32: put the Windows folders first.</summary>
    static SuggestedFix? WindowsFirst(Diagnosis diagnosis, DetectionContext context, PathDraft draft)
    {
        if (context.System32Position is not { } system32 || context.Entries[system32].Scope != PathScope.Machine) return null;

        bool IsWindows(DraftEntry e) => e.Origin is { } o
            && context.Snapshot.EntriesIn(o.Scope).FirstOrDefault(x => x.Index == o.Index) is { } scanned
            && DetectionContext.IsUnder(scanned.Key, context.SystemRootKey);

        var machine = draft.Machine;
        var windows = machine.Where(IsWindows).ToList();
        if (windows.Count == 0 || machine.Take(windows.Count).All(IsWindows)) return null;

        var lastWindows = machine.Select((e, i) => (e, i)).Last(p => IsWindows(p.e)).i;
        var ahead = machine.Take(lastWindows).Count(e => !IsWindows(e));
        var risky = diagnosis.Findings.Any(f => f.Rule == "SEC-04");
        return Fix(
            title: "Search the Windows folders first",
            edits: [new Reorder(PathScope.Machine, windows.Select(w => w.Id).ToList())],
            note: "A program ahead of them that shares a name with a Windows command stops winning. The command changes below say which.",
            recommended: false) with
        {
            Id = WindowsFirstId,
            Problem = $"{Words.Count(ahead, "entry", "entries")} in the machine PATH {(ahead == 1 ? "comes" : "come")} before Windows' own folders",
            Category = FindingCategory.Security,
            Severity = risky ? Severity.Medium : Severity.Low,
            NeedsAdmin = true,
        };
    }

    /// <summary>
    /// The recommended order for one scope: Windows folders, then folders only administrators can write, then
    /// writable ones, keeping the order within each. <paramref name="lockedDown"/> counts as locked down already.
    /// </summary>
    public static Reorder RecommendedOrder(PathDraft draft, PathScope scope, DetectionContext context, IEnumerable<string>? lockedDown = null)
    {
        var locked = (lockedDown ?? []).Select(PathText.Key).ToHashSet(StringComparer.Ordinal);
        int Rank(DraftEntry e)
        {
            if (e.Origin is not { } o || context.Snapshot.EntriesIn(o.Scope).FirstOrDefault(x => x.Index == o.Index) is not { } scanned)
                return 1;
            if (DetectionContext.IsUnder(scanned.Key, context.SystemRootKey)) return 0;
            var final = context.Resolve(scanned).Final;
            if (final is not null && locked.Contains(PathText.Key(final.Path))) return 1;
            return Threats.WhoCanPlant(final) == Attacker.None ? 1 : 2;
        }
        return new Reorder(scope, draft.EntriesIn(scope).Select((e, i) => (e, i)).OrderBy(p => Rank(p.e)).ThenBy(p => p.i).Select(p => p.e.Id).ToList());
    }

    static SuggestedFix Fix(string title, IEnumerable<EntryEdit>? edits = null, IEnumerable<AclFix>? acls = null,
        string? note = null, bool recommended = true) => new()
    {
        Id = "",
        Title = title,
        Problem = "",
        Note = note,
        Edits = edits?.ToList() ?? [],
        Acls = acls?.ToList() ?? [],
        Recommended = recommended,
    };

    static bool NeedsAdmin(SuggestedFix fix, DetectionContext context) =>
        fix.Edits.Any(e => TouchesMachine(e, context)) || fix.Acls.Any(a =>
            a.Scope == PathScope.Machine || a.ResetOwner
            || context.Snapshot.FactsFor(a.Folder)?.AccessFor(Perspective.CurrentUserUnelevated)?.CanWriteDac != true);

    static bool TouchesMachine(EntryEdit edit, DetectionContext context) => edit switch
    {
        SetKind k => k.Scope == PathScope.Machine,
        AddEntry a => a.Scope == PathScope.Machine,
        Reorder r => r.Scope == PathScope.Machine,
        MoveEntry m when m.Scope == PathScope.Machine => true,
        // An existing entry's id is its position in search order, so a machine entry's id is below the machine count.
        RemoveEntry or ReplaceText or TidyText or MoveEntry => IdOf(edit) < context.Snapshot.EntriesIn(PathScope.Machine)
            .Count(e => !e.Defects.HasFlag(HygieneDefects.TrailingSeparator)),
        _ => true,
    };

    static int IdOf(EntryEdit edit) => edit switch
    {
        RemoveEntry e => e.Id,
        ReplaceText e => e.Id,
        TidyText e => e.Id,
        MoveEntry e => e.Id,
        _ => -1,
    };

    static string Where(IReadOnlyList<EntryRef> entries) =>
        entries.Select(e => e.Scope).Distinct().Count() > 1 ? "PATH" : Words.Path(entries[0].Scope);

    static PathScope? ScopeOf(string rootCause) =>
        rootCause.EndsWith(":" + PathScope.Machine, StringComparison.Ordinal) ? PathScope.Machine
        : rootCause.EndsWith(":" + PathScope.User, StringComparison.Ordinal) ? PathScope.User
        : null;

    static string? FolderOf(Finding finding, DetectionContext context) =>
        finding.Entries.Select(e => context.Snapshot.EntriesIn(e.Scope).FirstOrDefault(x => x.Index == e.Index))
            .OfType<PathEntry>()
            .Select(e => context.Resolve(e).Final?.Path)
            .FirstOrDefault(p => p is not null);
}
