namespace Pathology.Core.Model;

/// <summary>
/// Everything one scan captured: immutable and serialisable. Detectors, shadowing and scoring are pure
/// functions of it, so a fixture JSON file is as good as the machine it came from.
/// </summary>
public sealed record PathSnapshot
{
    /// <summary>Bumped on any breaking change to the shape, so old fixtures and exports can be recognised.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>True once <see cref="Redaction.SnapshotRedactor"/> has scrubbed it (the only state that may be exported).</summary>
    public bool Redacted { get; init; }

    public HostInfo Host { get; init; } = new();

    public CaptureOptions Options { get; init; } = new();

    public RawPathValue MachinePath { get; init; } = RawPathValue.Missing(PathScope.Machine);

    public RawPathValue UserPath { get; init; } = RawPathValue.Missing(PathScope.User);

    /// <summary>One variable set per <see cref="EnvironmentSource"/>.</summary>
    public IReadOnlyList<EnvironmentVariables> Environment { get; init; } = [];

    /// <summary>The PATH a newly started process gets (<c>CreateEnvironmentBlock</c>).</summary>
    public string? EffectivePath { get; init; }

    /// <summary>This process's PATH. When it differs from <see cref="EffectivePath"/>, Explorer is stale.</summary>
    public string? ProcessPath { get; init; }

    /// <summary>The effective <c>PATHEXT</c>, in precedence order.</summary>
    public string? PathExt { get; init; }

    public IReadOnlyList<PerspectiveIdentity> Perspectives { get; init; } = [];

    /// <summary>Machine entries first, then user entries: the order a new process searches them.</summary>
    public IReadOnlyList<PathEntry> Entries { get; init; } = [];

    /// <summary>Facts for each probed entry, each missing entry's nearest existing ancestor, and each link target.</summary>
    public IReadOnlyList<DirectoryFacts> Directories { get; init; } = [];

    public RawPathValue PathFor(PathScope scope) => scope == PathScope.Machine ? MachinePath : UserPath;

    public IEnumerable<PathEntry> EntriesIn(PathScope scope) => Entries.Where(e => e.Scope == scope);

    public EnvironmentVariables? EnvironmentFrom(EnvironmentSource source) =>
        Environment.FirstOrDefault(e => e.Source == source);

    /// <summary>The facts captured for a path (case-insensitive), or null.</summary>
    public DirectoryFacts? FactsFor(string? path) =>
        path is null ? null : Directories.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

    public DirectoryFacts? FactsFor(PathEntry entry) => FactsFor(entry.ProbePath);

    public PerspectiveIdentity? IdentityOf(Perspective perspective) =>
        Perspectives.FirstOrDefault(p => p.Perspective == perspective);
}
