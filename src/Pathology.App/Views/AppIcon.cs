using Avalonia.Controls;
using Avalonia.Platform;

namespace Pathology.App.Views;

/// <summary>
/// The window and taskbar icon, shared by every window. It loads the multi-frame .ico rather than a single
/// large PNG: Windows asks for a 16px frame for the title bar and 24/32px for the taskbar, and a purpose-built
/// frame stays crisp where a 256 → 16 downscale goes muddy. Generated from pathology.svg by tools/gen-icons.ps1.
/// </summary>
static class AppIcon
{
    /// <summary>The icon, or null if it can't be loaded, so a missing asset never blocks startup.</summary>
    public static WindowIcon? Load()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://pathology/Assets/pathology.ico"));
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }
}
