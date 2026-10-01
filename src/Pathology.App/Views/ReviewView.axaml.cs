using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pathology.App.ViewModels;

namespace Pathology.App.Views;

public partial class ReviewView : UserControl
{
    public ReviewView() => InitializeComponent();

    /// <summary>Put the lock-downs on the clipboard as icacls commands, to read or run by hand.</summary>
    async void OnCopyCommands(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReviewViewModel { Plan: { HasCommandsText: true } plan } || sender is not Button button) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(plan.CommandsText);
        button.Content = "Copied";
        DispatcherTimer.RunOnce(() => button.Content = "Copy as icacls commands", TimeSpan.FromSeconds(1.5));
    }
}
