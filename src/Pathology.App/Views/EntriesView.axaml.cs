using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pathology.App.Controls;
using Pathology.App.ViewModels;

namespace Pathology.App.Views;

public partial class EntriesView : UserControl
{
    public EntriesView()
    {
        InitializeComponent();
        KeepSelectionVisible.Attach(this, nameof(EntriesViewModel.Selected), vm => ((EntriesViewModel)vm).Selected);
        // Right-clicking a line picks it out too, so the panel and the menu are about the same one.
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true)?.DataContext is EntryRowViewModel row)
            row.OpenCommand.Execute(null);
    }
}
