using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Pathology.App.ViewModels;

namespace Pathology.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Ask where to save, then export. Nothing is written until the user picks a file.</summary>
    async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export a redacted snapshot",
            SuggestedFileName = "pathology-snapshot.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Snapshot") { Patterns = ["*.json"] }],
        });
        if (file?.TryGetLocalPath() is { } path) vm.Export(path);
    }
}
