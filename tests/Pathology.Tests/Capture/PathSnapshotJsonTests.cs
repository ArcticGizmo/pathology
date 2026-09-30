using Pathology.Core.Model;
using Pathology.Core.Redaction;

namespace Pathology.Tests.Capture;

public class PathSnapshotJsonTests
{
    [Fact]
    public void A_snapshot_round_trips_through_JSON()
    {
        var original = SampleSnapshot.Build();

        var json = PathSnapshotJson.Serialize(original);
        var back = PathSnapshotJson.Deserialize(json);

        // Records compare collections by reference, so compare the re-serialised text instead.
        Assert.Equal(json, PathSnapshotJson.Serialize(back));
        Assert.Equal(PathValueKind.ExpandString, back.MachinePath.Kind);
        Assert.Equal(FileAccessRights.AddFile, back.Directories[0].Access[0].Granted);
        Assert.Equal(original.Directories[0].Sddl, back.Directories[0].Sddl);
    }

    [Fact]
    public void Enums_and_flags_are_written_as_names()
    {
        var json = PathSnapshotJson.Serialize(SampleSnapshot.Build());

        Assert.Contains("\"kind\": \"ExpandString\"", json);
        Assert.Contains("\"perspective\": \"CurrentUserUnelevated\"", json);
        Assert.Contains("\"attributes\": \"LogonId, Enabled\"", json);
        // Computed members stay out of the document.
        Assert.DoesNotContain("matchesAllowAces", json);
        Assert.DoesNotContain("allowSids", json);
        Assert.DoesNotContain("canAddFiles", json);
    }

    [Fact]
    public void Lookups_are_case_insensitive_after_a_round_trip()
    {
        var back = PathSnapshotJson.Deserialize(PathSnapshotJson.Serialize(SampleSnapshot.Build()));

        Assert.NotNull(back.FactsFor(@"c:\users\ALEX.EXAMPLE\bin"));
        Assert.Equal(SampleSnapshot.Profile, back.EnvironmentFrom(EnvironmentSource.NewProcess)!.ValueOf("userprofile"));
    }

    [Fact]
    public void Writing_an_unredacted_snapshot_is_refused()
    {
        using var store = new TempStore();
        var path = Path.Combine(store.Root, "snapshot.json");

        Assert.Throws<InvalidOperationException>(() => PathSnapshotJson.Write(path, SampleSnapshot.Build()));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_redacted_snapshot_writes_and_reads_back()
    {
        using var store = new TempStore();
        var path = Path.Combine(store.Root, "snapshot.json");
        var redacted = SnapshotRedactor.Redact(SampleSnapshot.Build());

        PathSnapshotJson.Write(path, redacted);

        var back = PathSnapshotJson.Read(path)!;
        Assert.True(back.Redacted);
        Assert.Equal(PathSnapshotJson.Serialize(redacted), PathSnapshotJson.Serialize(back));
    }
}
