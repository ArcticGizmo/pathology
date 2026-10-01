using Avalonia.Media;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using static Pathology.App.Theme;

namespace Pathology.App.Theming;

/// <summary>How severities, ratings, scopes and perspectives are named and coloured, the same on every page.</summary>
internal static class Severities
{
    public static string BrushKey(Severity? severity) => severity switch
    {
        Severity.High => NordTheme.HighBrush,
        Severity.Medium => NordTheme.MediumBrush,
        Severity.Low => NordTheme.LowBrush,
        Severity.Info => NordTheme.SeverityInfoBrush,
        _ => NordTheme.HealthyBrush,
    };

    /// <summary>The brush for a severity; null (a clean category) is green.</summary>
    public static IBrush BrushFor(Severity? severity) => Brush(BrushKey(severity));

    /// <summary>"high", "medium", "low", "note".</summary>
    public static string Word(Severity severity) => severity == Severity.Info ? "note" : severity.ToString().ToLowerInvariant();

    /// <summary>A category's rating as its card shows it: "HIGH" … "CLEAN".</summary>
    public static string RatingWord(Severity? rating) => rating is { } r ? Word(r).ToUpperInvariant() : "CLEAN";

    public static string PerspectiveName(Perspective perspective) => perspective switch
    {
        Perspective.CurrentUserUnelevated => "You",
        Perspective.CurrentUserElevated => "You, elevated",
        Perspective.System => "SYSTEM",
        Perspective.StandardUser => "A standard user",
        _ => perspective.ToString(),
    };

    /// <summary>The perspective mid-sentence: "you", "you elevated", "SYSTEM", "a standard user".</summary>
    public static string PerspectivePhrase(Perspective perspective) => perspective switch
    {
        Perspective.CurrentUserUnelevated => "you",
        Perspective.CurrentUserElevated => "you elevated",
        Perspective.System => "SYSTEM",
        Perspective.StandardUser => "a standard user",
        _ => perspective.ToString(),
    };

    /// <summary>"3 high · 1 medium", or "" when there's nothing.</summary>
    public static string Counts(Func<Severity, int> count) =>
        string.Join(" · ", new[] { Severity.High, Severity.Medium, Severity.Low }
            .Where(s => count(s) > 0)
            .Select(s => $"{count(s)} {Word(s)}"));
}
