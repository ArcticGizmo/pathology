using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Pathology.App.Controls;

/// <summary>
/// Scrolls a master/detail list to its selected row when the selection changes from elsewhere (another page
/// sends you to a finding or an entry that's below the fold).
/// </summary>
internal static class KeepSelectionVisible
{
    /// <param name="view">The page's view.</param>
    /// <param name="property">The view-model property holding the selected row.</param>
    /// <param name="selected">Reads that property.</param>
    public static void Attach(UserControl view, string property, Func<object, object?> selected)
    {
        INotifyPropertyChanged? watched = null;
        view.DataContextChanged += (_, _) =>
        {
            if (watched is not null) watched.PropertyChanged -= OnChanged;
            watched = view.DataContext as INotifyPropertyChanged;
            if (watched is not null) watched.PropertyChanged += OnChanged;
        };

        // Navigating to a page selects before its view exists, so scroll once it's shown, too.
        view.Loaded += (_, _) => Scroll(view.DataContext);

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == property) Scroll(sender);
        }

        void Scroll(object? vm)
        {
            if (vm is null || selected(vm) is not { } row) return;
            // After layout, so a freshly rebuilt list has its row containers.
            Dispatcher.UIThread.Post(() =>
                view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => ReferenceEquals(b.DataContext, row))?.BringIntoView(),
                DispatcherPriority.Background);
        }
    }
}
