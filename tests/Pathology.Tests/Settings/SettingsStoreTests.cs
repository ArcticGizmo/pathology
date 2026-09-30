using Pathology.Core.Settings;

namespace Pathology.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void Get_returns_defaults_when_nothing_saved()
    {
        using var store = new TempStore();
        var settings = new FileSettingsStore(store.Paths).Get();

        Assert.True(settings.ShowChangelogOnUpdate);
        Assert.False(settings.ProbeNetworkPaths);   // opt-in only: probing a UNC path leaks an NTLM hash
        Assert.Null(settings.LastSeenVersion);
    }

    [Fact]
    public void Save_then_get_round_trips()
    {
        using var store = new TempStore();
        var sut = new FileSettingsStore(store.Paths);

        sut.Save(new PathologySettings { ShowChangelogOnUpdate = false, ProbeNetworkPaths = true, LastSeenVersion = "0.4.1" });

        var loaded = sut.Get();
        Assert.False(loaded.ShowChangelogOnUpdate);
        Assert.True(loaded.ProbeNetworkPaths);
        Assert.Equal("0.4.1", loaded.LastSeenVersion);
    }

    [Fact]
    public void A_corrupt_settings_file_reads_as_defaults_rather_than_throwing()
    {
        using var store = new TempStore();
        File.WriteAllText(store.Paths.SettingsFile, "{ this is not json");

        var settings = new FileSettingsStore(store.Paths).Get();

        Assert.True(settings.ShowChangelogOnUpdate);
    }

    [Fact]
    public void Save_creates_the_store_directory_when_missing()
    {
        using var store = new TempStore();
        var nested = new Pathology.Core.Store.PathologyPaths(Path.Combine(store.Root, "not-yet"));

        new FileSettingsStore(nested).Save(new PathologySettings { LastSeenVersion = "1.0.0" });

        Assert.True(File.Exists(nested.SettingsFile));
    }
}
