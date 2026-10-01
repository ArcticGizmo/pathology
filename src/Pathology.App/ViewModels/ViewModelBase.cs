using CommunityToolkit.Mvvm.ComponentModel;

namespace Pathology.App.ViewModels;

/// <summary>Base for all view-models (INotifyPropertyChanged via CommunityToolkit.Mvvm).</summary>
public abstract class ViewModelBase : ObservableObject;

/// <summary>A top-level navigable page.</summary>
public abstract partial class PageViewModel : ViewModelBase
{
    /// <summary>Nav label / header for the page.</summary>
    public abstract string Title { get; }

    /// <summary>Shown indented in the nav, under a <see cref="NavGroupViewModel"/>.</summary>
    public virtual bool IsNested => false;

    /// <summary>True when this is the page currently shown — drives the left-nav highlight.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Count shown as a badge next to the nav label (0 = hidden). Each page sets it as it loads.</summary>
    [ObservableProperty] private int _navCount;

    /// <summary>Whether the nav badge should be shown (hidden at zero).</summary>
    public bool ShowNavCount => NavCount > 0;

    partial void OnNavCountChanged(int value) => OnPropertyChanged(nameof(ShowNavCount));

    partial void OnIsActiveChanged(bool value)
    {
        if (value) OnActivated();
    }

    /// <summary>Called when this page becomes the current page. Override to refresh live data.</summary>
    protected virtual void OnActivated() { }
}

/// <summary>
/// A nav row that holds the pages nested under it ("Entries" over System and User). It isn't a page itself:
/// clicking it opens its first page, and it reads as active while any of them is showing.
/// </summary>
public sealed partial class NavGroupViewModel : ObservableObject
{
    public NavGroupViewModel(string label, IReadOnlyList<PageViewModel> pages)
    {
        Label = label;
        Pages = pages;
        foreach (var page in pages)
            page.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PageViewModel.IsActive)) OnPropertyChanged(nameof(IsActive));
            };
    }

    public string Label { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public bool IsActive => Pages.Any(p => p.IsActive);
}
