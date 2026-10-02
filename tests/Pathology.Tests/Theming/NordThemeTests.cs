using ArcticGizmo.Avalonia.Palette;
using Pathology.App.Theming;

namespace Pathology.Tests.Theming;

public class NordThemeTests
{
    [Fact]
    public void The_palette_id_exists_and_is_the_dark_nord()
    {
        // Guards a package bump: a renamed id would throw at startup, before any window opens.
        var palette = PaletteCatalog.ById(NordTheme.PaletteId);

        Assert.Equal("Nord", palette.Family);
        Assert.True(palette.IsDark);
    }

    [Theory]
    // Every house key the views paint with must be published by the package itself — a key that silently
    // stops existing renders as nothing (a DynamicResource miss isn't an error).
    [InlineData("FormBgBrush")]
    [InlineData("PanelBgBrush")]
    [InlineData("FgBrush")]
    [InlineData("TitleBrush")]
    [InlineData("MutedBrush")]
    [InlineData("AccentBrush")]
    [InlineData("OnAccentBrush")]
    [InlineData("BorderBrush")]
    [InlineData("ButtonBgBrush")]
    [InlineData("OkBrush")]
    [InlineData("WarnBrush")]
    [InlineData("DangerBrush")]
    [InlineData("DevBrush")]
    [InlineData("NavBgBrush")]
    [InlineData("NavItemTextBrush")]
    [InlineData("NavItemHoverBrush")]
    [InlineData("NavItemActiveBgBrush")]
    [InlineData("NavItemActiveTextBrush")]
    [InlineData("NavSectionBrush")]
    public void The_house_brush_keys_are_built_in_tokens(string key)
        => Assert.Contains(key, ThemeTokens.All);

    [Fact]
    public void The_severity_tokens_do_not_collide_with_built_ins()
    {
        // A collision would silently override the package's own derivation of that key.
        foreach (var key in NordTheme.AppTokens)
            Assert.DoesNotContain(key, ThemeTokens.All);
    }
}
