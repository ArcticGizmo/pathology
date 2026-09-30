using Pathology.Core.Store;

namespace Pathology.Tests.Store;

public class AppProfileTests
{
    [Fact]
    public void The_store_folder_is_never_the_velopack_install_folder()
    {
        // Velopack installs to %LOCALAPPDATA%\<packId> = %LOCALAPPDATA%\Pathology, and an uninstall removes
        // that folder wholesale. Paths are case-insensitive on Windows, so the store must differ by more than
        // case or settings (and, from M6, PATH backups) die with the uninstall.
        Assert.False(string.Equals(AppProfile.DataFolderName, "Pathology", StringComparison.OrdinalIgnoreCase));
        Assert.False(AppProfile.DataFolderName.StartsWith(@"Pathology\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_default_store_lives_under_local_app_data()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var paths = new PathologyPaths();

        Assert.Equal(Path.Combine(local, AppProfile.DataFolderName), paths.Root);
        Assert.Equal(Path.Combine(paths.Root, "settings.json"), paths.SettingsFile);
    }
}
