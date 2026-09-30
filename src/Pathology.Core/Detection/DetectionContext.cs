using Pathology.Core.Capture;
using Pathology.Core.Model;
using Pathology.Core.Normalisation;
using Pathology.Core.Shadowing;

namespace Pathology.Core.Detection;

/// <summary>An entry's folder, followed through any junctions or symlinks to where files really land.</summary>
/// <param name="Entry">The PATH entry.</param>
/// <param name="Own">The facts for the entry's own path, or null when it wasn't probed (relative, network, …).</param>
/// <param name="Final">The facts for the end of the link chain (equal to <paramref name="Own"/> without a link).</param>
/// <param name="Links">The links passed through, in order. Empty when the entry isn't a link.</param>
public sealed record ResolvedEntry(PathEntry Entry, DirectoryFacts? Own, DirectoryFacts? Final, IReadOnlyList<DirectoryFacts> Links)
{
    public bool ViaLink => Links.Count > 0;
}

/// <summary>
/// A snapshot plus what every detector needs worked out once: entries resolved through links, where System32
/// sits in the search order, who counts as an administrator, and the shadow report. Pure: built from the
/// snapshot alone.
/// </summary>
public sealed class DetectionContext
{
    readonly Lazy<ShadowReport> _shadows;
    readonly Dictionary<PathEntry, ResolvedEntry> _resolved = new(ReferenceEqualityComparer.Instance);

    public DetectionContext(PathSnapshot snapshot)
    {
        Snapshot = snapshot;
        _shadows = new(() => ShadowEngine.Build(this));

        var systemRoot = Variable("SystemRoot") ?? Variable("windir") ?? @"C:\Windows";
        SystemRootKey = KeyOf(systemRoot);
        System32Key = KeyOf(systemRoot.TrimEnd('\\') + @"\System32");

        var profile = snapshot.Host.UserProfile;
        UserProfileKey = profile.Length > 3 ? KeyOf(profile) : null;
        ProfilesRootKey = profile.Length > 3 && PathText.Canonical(PathText.Strip(profile)) is { } p && PathText.Parent(p) is { } parent
            ? PathText.Key(parent)
            : @"C:\USERS";

        var elevated = snapshot.IdentityOf(Perspective.CurrentUserElevated);
        UserIsAdmin = elevated?.AllowSids.Contains(WellKnownSids.Administrators, StringComparer.OrdinalIgnoreCase) == true;
        HasSplitToken = snapshot.Host.Elevation is ElevationType.Limited or ElevationType.Full;

        PathExt = (snapshot.PathExt ?? SnapshotCapturer.DefaultPathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.StartsWith('.'))
            .Select(e => e.ToUpperInvariant())
            .Distinct()
            .ToList();
    }

    public PathSnapshot Snapshot { get; }

    /// <summary>Every entry in search order: machine, then user.</summary>
    public IReadOnlyList<PathEntry> Entries => Snapshot.Entries;

    public string SystemRootKey { get; }
    public string System32Key { get; }

    /// <summary>This user's profile folder, as a duplicate-detection key.</summary>
    public string? UserProfileKey { get; }

    /// <summary>The folder profiles live in (<c>C:\Users</c>), as a key.</summary>
    public string ProfilesRootKey { get; }

    /// <summary>The user's elevated token holds Administrators: an unelevated write is a UAC bypass, not an escalation.</summary>
    public bool UserIsAdmin { get; }

    /// <summary>This logon has a UAC split token, so elevated sessions exist beside unelevated ones.</summary>
    public bool HasSplitToken { get; }

    /// <summary><c>PATHEXT</c>, upper-cased, in precedence order.</summary>
    public IReadOnlyList<string> PathExt { get; }

    public ShadowReport Shadows => _shadows.Value;

    /// <summary>Position in the search order of the first System32 entry, or null when PATH lacks it.</summary>
    public int? System32Position => IndexOf(System32Key);

    public int? IndexOf(string key)
    {
        for (var i = 0; i < Entries.Count; i++)
            if (Entries[i].Key == key) return i;
        return null;
    }

    public ResolvedEntry Resolve(PathEntry entry)
    {
        if (_resolved.TryGetValue(entry, out var known)) return known;

        var own = Snapshot.FactsFor(entry);
        var links = new List<DirectoryFacts>();
        var final = own;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (final is { ReparseTarget: { } target } && links.Count < SnapshotCapturer.MaxLinkHops && seen.Add(final.Path))
        {
            links.Add(final);
            var next = PathText.Canonical(PathText.Strip(target)) is { } canonical ? Snapshot.FactsFor(canonical) : null;
            if (next is null) { final = null; break; }
            final = next;
        }

        return _resolved[entry] = new ResolvedEntry(entry, own, final, links);
    }

    /// <summary>A variable's value as a new process sees it, falling back to the machine scope.</summary>
    public string? Variable(string name) =>
        Snapshot.EnvironmentFrom(EnvironmentSource.NewProcess)?.ValueOf(name)
        ?? Snapshot.EnvironmentFrom(EnvironmentSource.Machine)?.ValueOf(name);

    public bool Defines(EnvironmentSource source, string name) => Snapshot.EnvironmentFrom(source)?.Defines(name) == true;

    /// <summary>True when <paramref name="key"/> is <paramref name="folderKey"/> or inside it.</summary>
    public static bool IsUnder(string key, string folderKey) =>
        key == folderKey || key.StartsWith(folderKey.TrimEnd('\\') + @"\", StringComparison.Ordinal);

    public static string KeyOf(string path)
    {
        var stripped = PathText.Strip(path);
        return PathText.Key(PathText.Canonical(stripped) ?? stripped);
    }
}
