using Pathology.Core.Capture;
using Pathology.Core.Model;
using Pathology.Core.Normalisation;
using Pathology.Core.Redaction;

namespace Pathology.Windows.Tests;

/// <summary>
/// The whole capture with the real probe, perspectives and evaluator. The first test feeds it a made-up PATH
/// that points into a temp tree; the second reads this machine's real PATH, read-only, and asserts only on its
/// shape. Neither prints a path, name or SID.
/// </summary>
public sealed class CaptureTests : IDisposable
{
    readonly TempTree _tree = new();
    readonly AccessEvaluator _evaluator = new();

    [Fact]
    public void A_made_up_PATH_over_a_temp_tree_is_captured_end_to_end()
    {
        var bin = _tree.Folder("bin");
        var real = _tree.Folder("real");
        _tree.Junction(_tree.PathOf("link"), real);
        var path = string.Join(';', bin, _tree.PathOf(@"missing\deeper"), @"\\pathology-test.invalid\share", @"relative\dir", "", _tree.PathOf("link"));

        var snapshot = new SnapshotCapturer(new FixedRegistry(path), new EmptyEnvironment(), new HostInfoReader(),
            new TokenPerspectives(), new DirectoryProbe(), _evaluator).Capture();

        Assert.Equal(6, snapshot.Entries.Count);
        Assert.True(snapshot.FactsFor(snapshot.Entries[0])!.Exists);

        var missing = snapshot.FactsFor(snapshot.Entries[1])!;
        Assert.False(missing.Exists);
        Quiet.Same(_tree.Root, missing.NearestExistingAncestor, "the nearest existing ancestor");
        Assert.NotEmpty(snapshot.FactsFor(_tree.Root)!.Access);

        Assert.Equal(ProbeStatus.SkippedNetwork, snapshot.FactsFor(snapshot.Entries[2])!.Status);
        Assert.Null(snapshot.Entries[3].ProbePath);
        Assert.Null(snapshot.Entries[4].ProbePath);

        Assert.True(snapshot.FactsFor(snapshot.Entries[5])!.IsJunction);
        Assert.True(snapshot.FactsFor(real)!.Exists);

        foreach (var facts in snapshot.Directories.Where(d => d.Sddl is not null))
        {
            Assert.Equal(Enum.GetValues<Perspective>(), facts.Access.Select(a => a.Perspective));
            Assert.All(facts.Access, a => Assert.True(a.Evaluated, a.Error));
        }
        Assert.False(missing.Exists || Directory.Exists(_tree.PathOf("missing")));
    }

    [Fact]
    public void This_machine_captures_cleanly_and_redacts_without_leaks()
    {
        var snapshot = WindowsCapture.Create(_evaluator).Capture(new CaptureOptions { ProbeNetworkPaths = false });

        Assert.Equal(PathTokeniser.Split(snapshot.MachinePath.Value).Count, snapshot.EntriesIn(PathScope.Machine).Count());
        Assert.Equal(PathTokeniser.Split(snapshot.UserPath.Value).Count, snapshot.EntriesIn(PathScope.User).Count());
        Assert.Equal(4, snapshot.Perspectives.Count);
        Assert.False(string.IsNullOrEmpty(snapshot.EffectivePath));

        foreach (var entry in snapshot.Entries.Where(e => e.ProbePath is not null))
            Assert.NotNull(snapshot.FactsFor(entry));
        Assert.DoesNotContain(snapshot.Directories, d => d.Drive is DriveKind.Unc or DriveKind.MappedNetwork && d.Status == ProbeStatus.Probed);
        Assert.All(snapshot.Directories.SelectMany(d => d.Access), a => Assert.True(a.Evaluated, "an access check failed"));

        // Windows' own folders are always on PATH and are never writable by a standard user.
        var system32 = snapshot.Directories.FirstOrDefault(d => d.Path.EndsWith(@"\System32", StringComparison.OrdinalIgnoreCase) && d.Exists);
        Assert.NotNull(system32);
        Assert.False(system32!.AccessFor(Perspective.StandardUser)!.CanPlant);

        var redacted = SnapshotRedactor.Redact(snapshot);
        var leaks = SnapshotRedactor.FindLeaks(PathSnapshotJson.Serialize(redacted), snapshot.Host);
        Assert.True(leaks.Count == 0, "redaction left: " + string.Join(", ", leaks));   // categories only, never values
    }

    public void Dispose()
    {
        _evaluator.Dispose();
        _tree.Dispose();
    }

    sealed class FixedRegistry(string machinePath) : IRegistryPathReader
    {
        public RegistryEnvironment Read() => new(
            new RawPathValue { Scope = PathScope.Machine, Kind = PathValueKind.ExpandString, Value = machinePath },
            RawPathValue.Missing(PathScope.User),
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>());
    }

    sealed class EmptyEnvironment : IEffectiveEnvironmentReader
    {
        public IReadOnlyDictionary<string, string> ReadNewProcessEnvironment() => new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> ReadCurrentProcessEnvironment() => new Dictionary<string, string>();
    }
}
