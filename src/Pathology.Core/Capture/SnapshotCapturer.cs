using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Core.Capture;

/// <summary>The stages of a capture, in order (they become the operation checklist in M4).</summary>
public enum CaptureStep
{
    ReadingRegistry,
    ReadingEnvironment,
    ResolvingPerspectives,
    ProbingDirectories,
    ListingCommands,
    EvaluatingAccess,
    Done,
}

/// <summary>Progress through a capture. <see cref="Total"/> is zero for steps that aren't counted.</summary>
public sealed record CaptureProgress(CaptureStep Step, int Completed = 0, int Total = 0);

/// <summary>
/// Takes a <see cref="PathSnapshot"/>: reads the registry and environments, tokenises and expands both PATH
/// values, probes each directory (plus the nearest existing ancestor of a missing one, and every link
/// target), lists the command files in each entry's folder, and evaluates each captured security descriptor
/// for every perspective.
/// </summary>
/// <remarks>
/// The OS work is behind the reader interfaces, so this orchestration, including the rule that network paths
/// are left alone, is tested with fakes. Nothing here writes anything.
/// </remarks>
public sealed class SnapshotCapturer(
    IRegistryPathReader registry,
    IEffectiveEnvironmentReader environment,
    IHostInfoReader host,
    ITokenPerspectives perspectives,
    IDirectoryProbe probe,
    IAccessEvaluator evaluator,
    TimeProvider? clock = null)
{
    /// <summary>How many link hops to follow before giving up (a junction loop must not hang a scan).</summary>
    public const int MaxLinkHops = 8;

    /// <summary>What <c>PATHEXT</c> is when nothing sets it (Windows' own default).</summary>
    public const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC";

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public PathSnapshot Capture(
        CaptureOptions? options = null, IProgress<CaptureProgress>? progress = null, CancellationToken cancel = default)
    {
        options ??= new CaptureOptions();

        progress?.Report(new(CaptureStep.ReadingRegistry));
        var reg = registry.Read();
        var hostInfo = host.Read();
        cancel.ThrowIfCancellationRequested();

        progress?.Report(new(CaptureStep.ReadingEnvironment));
        var newProcess = environment.ReadNewProcessEnvironment();
        var current = environment.ReadCurrentProcessEnvironment();
        cancel.ThrowIfCancellationRequested();

        progress?.Report(new(CaptureStep.ResolvingPerspectives));
        var identities = perspectives.Build();

        var drafts = Draft(reg.MachinePath, MachineLookup(reg, newProcess))
            .Concat(Draft(reg.UserPath, name => Lookup(newProcess, name)))
            .ToList();

        var pathExt = Lookup(newProcess, "PATHEXT") ?? Lookup(reg.MachineVariables, "PATHEXT");
        var directories = ProbeAll(drafts, options, progress, cancel);
        ListAll(directories, drafts, CommandExtensions(pathExt), options, progress, cancel);
        var evaluated = EvaluateAll(directories, identities, progress, cancel);

        var entries = drafts
            .Select(e => e.ProbePath is { } p && directories.TryGetValue(p, out var f) && f.LongPath is { } longPath
                ? e with { Key = PathText.Key(longPath) }
                : e)
            .ToList();

        var relevant = EnvironmentCapturePolicy.RelevantNames(
            [reg.MachinePath.Value, reg.UserPath.Value],
            [reg.MachineVariables, reg.UserVariables, reg.VolatileVariables, newProcess]);

        progress?.Report(new(CaptureStep.Done));
        return new PathSnapshot
        {
            CapturedAt = _clock.GetUtcNow(),
            Host = hostInfo,
            Options = options,
            MachinePath = reg.MachinePath,
            UserPath = reg.UserPath,
            Environment =
            [
                EnvironmentCapturePolicy.Select(EnvironmentSource.Machine, reg.MachineVariables, relevant),
                EnvironmentCapturePolicy.Select(EnvironmentSource.User, reg.UserVariables, relevant),
                EnvironmentCapturePolicy.Select(EnvironmentSource.Volatile, reg.VolatileVariables, relevant),
                EnvironmentCapturePolicy.Select(EnvironmentSource.NewProcess, newProcess, relevant),
                EnvironmentCapturePolicy.Select(EnvironmentSource.CurrentProcess, current, relevant),
            ],
            EffectivePath = Lookup(newProcess, "Path"),
            ProcessPath = Lookup(current, "Path"),
            PathExt = pathExt,
            Perspectives = identities,
            Entries = entries,
            Directories = evaluated,
        };
    }

    /// <summary>Tokenise and expand one stored value. A <c>REG_SZ</c> value is left literal, as Windows leaves it.</summary>
    static IEnumerable<PathEntry> Draft(RawPathValue value, Func<string, string?> lookup)
    {
        foreach (var segment in PathTokeniser.Split(value.Value))
        {
            Expansion expansion;
            if (value.Expands)
            {
                expansion = EnvironmentExpander.Expand(segment.Text, lookup);
            }
            else
            {
                var names = EnvironmentExpander.References(segment.Text);
                expansion = new Expansion(segment.Text, names, names);
            }

            var stripped = PathText.Strip(expansion.Text);
            var canonical = PathText.Canonical(stripped);
            yield return new PathEntry
            {
                Scope = value.Scope,
                Index = segment.Index,
                Raw = segment.Text,
                Expanded = expansion.Text,
                Form = PathText.Classify(stripped),
                Defects = segment.Defects,
                Variables = expansion.Referenced,
                UnresolvedVariables = expansion.Unresolved,
                ProbePath = canonical,
                Key = PathText.Key(canonical ?? stripped),
            };
        }
    }

    /// <summary>
    /// How the machine PATH is expanded: system variables are set before the user's, so a variable defined
    /// only in <c>HKCU\Environment</c> isn't visible yet and stays literal, and where both scopes define one the
    /// machine value applies. Profile and volatile values (<c>USERPROFILE</c>, <c>APPDATA</c>, …) are visible.
    /// </summary>
    static Func<string, string?> MachineLookup(RegistryEnvironment reg, IReadOnlyDictionary<string, string> newProcess) =>
        name =>
        {
            if (Lookup(reg.MachineVariables, name) is { } machine)
                return Lookup(reg.UserVariables, name) is null
                    ? Lookup(newProcess, name) ?? machine
                    : EnvironmentExpander.Expand(machine, n => Lookup(newProcess, n)).Text;
            if (Lookup(reg.UserVariables, name) is not null && Lookup(reg.VolatileVariables, name) is null)
                return null;
            return Lookup(newProcess, name);
        };

    Dictionary<string, DirectoryFacts> ProbeAll(
        List<PathEntry> drafts, CaptureOptions options, IProgress<CaptureProgress>? progress, CancellationToken cancel)
    {
        var facts = new Dictionary<string, DirectoryFacts>(StringComparer.OrdinalIgnoreCase);
        var paths = drafts.Select(e => e.ProbePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        for (var i = 0; i < paths.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new(CaptureStep.ProbingDirectories, i, paths.Count));
            ProbeWithContext(paths[i], hops: 0);
        }
        progress?.Report(new(CaptureStep.ProbingDirectories, paths.Count, paths.Count));
        return facts;

        // Probe a path, then what its findings depend on: a missing folder's nearest existing ancestor, and a
        // link's target (the probe has already refused to follow one that leads to the network).
        void ProbeWithContext(string path, int hops)
        {
            if (facts.ContainsKey(path)) return;
            var found = ProbeOnce(path);

            if (found is { Status: ProbeStatus.Probed, Exists: false })
            {
                string? ancestor = null;
                for (var parent = PathText.Parent(path); parent is not null; parent = PathText.Parent(parent))
                {
                    var p = facts.TryGetValue(parent, out var known) ? known : facts[parent] = ProbeOnce(parent);
                    if (p.Status != ProbeStatus.Probed) break;
                    if (p.Exists) { ancestor = parent; break; }
                }
                found = found with { NearestExistingAncestor = ancestor };
            }
            facts[path] = found;

            if (found.ReparseTarget is { } target && hops < MaxLinkHops
                && PathText.Canonical(PathText.Strip(target)) is { } canonicalTarget)
                ProbeWithContext(canonicalTarget, hops + 1);
        }

        DirectoryFacts ProbeOnce(string path)
        {
            try
            {
                return probe.Probe(path, options.ProbeNetworkPaths) with { Path = path };
            }
            catch (Exception ex)
            {
                return new DirectoryFacts { Path = path, Status = ProbeStatus.Failed, Note = ex.Message };
            }
        }
    }

    /// <summary>
    /// The extensions worth listing: <c>PATHEXT</c>'s (cmd's and the loader's view) plus <c>.ps1</c>, which
    /// PowerShell also finds on PATH.
    /// </summary>
    public static IReadOnlySet<string> CommandExtensions(string? pathExt)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".PS1" };
        foreach (var ext in (pathExt ?? DefaultPathExt).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (ext.StartsWith('.')) set.Add(ext);
        return set;
    }

    /// <summary>List the command files in each existing entry folder (not ancestors or link targets: an entry's
    /// own listing already goes through its link).</summary>
    void ListAll(
        Dictionary<string, DirectoryFacts> facts, List<PathEntry> drafts, IReadOnlySet<string> extensions,
        CaptureOptions options, IProgress<CaptureProgress>? progress, CancellationToken cancel)
    {
        var folders = drafts.Select(e => e.ProbePath).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => facts.TryGetValue(p, out var f) && f is { Status: ProbeStatus.Probed, Exists: true, IsDirectory: true, ReparseTargetIsNetwork: false })
            .ToList();

        for (var i = 0; i < folders.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new(CaptureStep.ListingCommands, i, folders.Count));
            IReadOnlyList<string>? files;
            try { files = probe.ListFiles(folders[i], extensions, options.ProbeNetworkPaths); }
            catch (Exception) { files = null; }
            facts[folders[i]] = facts[folders[i]] with { CommandFiles = files };
        }
        progress?.Report(new(CaptureStep.ListingCommands, folders.Count, folders.Count));
    }

    List<DirectoryFacts> EvaluateAll(
        Dictionary<string, DirectoryFacts> facts, IReadOnlyList<PerspectiveIdentity> identities,
        IProgress<CaptureProgress>? progress, CancellationToken cancel)
    {
        var withSd = facts.Values.Count(f => f.Sddl is not null);
        var done = 0;
        var result = new List<DirectoryFacts>(facts.Count);
        foreach (var f in facts.Values)
        {
            if (f.Sddl is not { } sddl) { result.Add(f); continue; }
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new(CaptureStep.EvaluatingAccess, done++, withSd));
            result.Add(f with { Access = identities.Select(id => EvaluateOne(sddl, id)).ToList() });
        }
        progress?.Report(new(CaptureStep.EvaluatingAccess, withSd, withSd));
        return result;
    }

    AccessResult EvaluateOne(string sddl, PerspectiveIdentity identity)
    {
        try { return evaluator.Evaluate(sddl, identity); }
        catch (Exception ex) { return new AccessResult { Perspective = identity.Perspective, Error = ex.Message }; }
    }

    static string? Lookup(IReadOnlyDictionary<string, string> source, string name)
    {
        if (source.TryGetValue(name, out var value)) return value;
        foreach (var (key, v) in source)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }
}
