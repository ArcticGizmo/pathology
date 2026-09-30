using ArcticGizmo.Avalonia.Palette;
using Avalonia;

namespace Pathology.App.Theming;

/// <summary>
/// PATHology's one theme: Nord (Dark), from <c>ArcticGizmo.Avalonia.Palette</c>.
/// <para>
/// The package publishes emuwren's house brush keys (<c>FormBgBrush</c>, <c>PanelBgBrush</c>, <c>FgBrush</c>,
/// <c>AccentBrush</c>, <c>OkBrush</c> / <c>WarnBrush</c> / <c>DangerBrush</c>, the nav tokens, …) as first-class
/// tokens, so views are written exactly as emuwren's are. On top of those, PATHology registers the
/// <b>severity</b> brushes its findings and health bands need. They are <em>derived</em> from the palette
/// (not pinned hex) so they stay inside the package's WCAG-AA gate, and would follow a palette picker if one
/// is ever added.
/// </para>
/// <para>
/// Never replace a brush instance in <c>Application.Resources</c>: the engine recolours them in place.
/// Paint with <c>{DynamicResource …}</c> or look them up via <see cref="Pathology.App.Theme.Brush"/>.
/// </para>
/// </summary>
internal static class NordTheme
{
    /// <summary>The palette id in <see cref="PaletteCatalog"/>.</summary>
    public const string PaletteId = "nord-dark";

    /// <summary>Critical findings and the "At risk" health band.</summary>
    public const string CriticalBrush = "CriticalBrush";

    /// <summary>High findings and the "Needs attention" band — Nord's aurora orange, between red and yellow.</summary>
    public const string HighBrush = "HighBrush";

    /// <summary>Medium findings and the "Fair" band.</summary>
    public const string MediumBrush = "MediumBrush";

    /// <summary>Low findings.</summary>
    public const string LowBrush = "LowBrush";

    /// <summary>Informational findings.</summary>
    public const string SeverityInfoBrush = "SeverityInfoBrush";

    /// <summary>The "Healthy" band and clean entries.</summary>
    public const string HealthyBrush = "HealthyBrush";

    /// <summary>Every token PATHology adds on top of the package's built-ins.</summary>
    public static IReadOnlyList<string> AppTokens { get; } =
        [CriticalBrush, HighBrush, MediumBrush, LowBrush, SeverityInfoBrush, HealthyBrush];

    /// <summary>
    /// Registers the severity tokens and applies Nord (Dark). Call once from
    /// <c>App.OnFrameworkInitializationCompleted</c>, before any window is built — including on the headless
    /// render path, which has no desktop lifetime.
    /// </summary>
    public static void Apply(Application app)
    {
        ThemeManager.RegisterTokens(
            TokenSpec.Derived(CriticalBrush, p => p.Danger),
            TokenSpec.Derived(HighBrush, p => p.Danger.MixWith(p.Warning, 0.5)),
            TokenSpec.Derived(MediumBrush, p => p.Warning),
            TokenSpec.Derived(LowBrush, p => p.Info),
            TokenSpec.Derived(SeverityInfoBrush, p => p.TextMuted),
            TokenSpec.Derived(HealthyBrush, p => p.Success));

        ThemeManager.Initialize(app, PaletteCatalog.ById(PaletteId));
    }
}
