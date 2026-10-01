using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.Core.Learn;

namespace Pathology.App.ViewModels;

/// <summary>
/// Short notes on how Windows really resolves commands and DLLs. Findings link here by topic, so each article
/// can be opened directly (<see cref="Open"/>).
/// </summary>
public sealed partial class LearnViewModel : PageViewModel
{
    public LearnViewModel()
    {
        Articles = LearnLibrary.All.Select(a => new LearnArticleRowViewModel(a, this)).ToList();
        Selected = Articles.FirstOrDefault();
    }

    public override string Title => "Learn";

    public IReadOnlyList<LearnArticleRowViewModel> Articles { get; }

    [ObservableProperty] private LearnArticleRowViewModel? _selected;

    partial void OnSelectedChanged(LearnArticleRowViewModel? oldValue, LearnArticleRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    /// <summary>Show the article for a <see cref="Core.Detection.LearnTopics"/> id.</summary>
    public void Open(string topic)
    {
        if (Articles.FirstOrDefault(a => string.Equals(a.Article.Id, topic, StringComparison.OrdinalIgnoreCase)) is { } row)
            Selected = row;
    }
}

public sealed partial class LearnArticleRowViewModel(LearnArticle article, LearnViewModel owner) : ViewModelBase
{
    public LearnArticle Article { get; } = article;
    public string Title => Article.Title;
    public string Summary => Article.Summary;
    public string Markdown => Article.Markdown;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Open() => owner.Selected = this;
}
