namespace Pathology.App.ViewModels;

/// <summary>
/// A page that exists in the nav but isn't built yet — the M0 stand-in for Health, Findings, Entries,
/// Shadowing and Learn. It says what the page will do and which milestone brings it, so the shell and its
/// navigation can ship (and be released) before any scanning exists. Each one is replaced by its real
/// view-model in M4; see docs/implementation-plan.md.
/// </summary>
public sealed class PlaceholderPageViewModel(string title, string heading, string summary, string milestone,
    IReadOnlyList<string> bullets) : PageViewModel
{
    public override string Title { get; } = title;

    /// <summary>The page's headline, e.g. "PATH health".</summary>
    public string Heading { get; } = heading;

    /// <summary>One line on what the page is for.</summary>
    public string Summary { get; } = summary;

    /// <summary>Which milestone builds it, e.g. "Arrives in M4".</summary>
    public string Milestone { get; } = milestone;

    /// <summary>What it will show.</summary>
    public IReadOnlyList<string> Bullets { get; } = bullets;
}
