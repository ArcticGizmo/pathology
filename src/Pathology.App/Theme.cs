using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pathology.App;

/// <summary>
/// Looks the palette (registered by <see cref="Theming.NordTheme"/>) up by key from code. Views bind brushes in
/// XAML; this is for the places that can't — view-models that expose a state-derived brush, and windows
/// built in code rather than markup.
/// </summary>
internal static class Theme
{
    /// <summary>The brush for <paramref name="key"/>, or grey when there's no application (a unit test).</summary>
    public static IBrush Brush(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;
}
