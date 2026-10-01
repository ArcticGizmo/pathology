using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pathology.App.Controls;
using Pathology.App.ViewModels;

namespace Pathology.App.Views;

public partial class FindingsView : UserControl
{
    public FindingsView()
    {
        InitializeComponent();
        KeepSelectionVisible.Attach(this, nameof(FindingsViewModel.Selected), vm => ((FindingsViewModel)vm).Selected);
    }

    /// <summary>Put the selected finding on the clipboard as plain text, for a ticket.</summary>
    async void OnCopyDetails(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FindingsViewModel { Detail: { } detail } || sender is not Button button) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(detail.DetailsText);
        button.Content = "Copied";
        DispatcherTimer.RunOnce(() => button.Content = "Copy details", TimeSpan.FromSeconds(1.5));
    }
}
