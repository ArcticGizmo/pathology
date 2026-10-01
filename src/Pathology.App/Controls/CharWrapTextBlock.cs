using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Pathology.App.Controls;

/// <summary>
/// A <see cref="SelectableTextBlock"/> that wraps at <em>any</em> character, not just at the word boundaries
/// Avalonia's <c>TextWrapping="Wrap"</c> prefers. Ported from emuwren.
/// </summary>
/// <remarks>
/// Long paths are the reason this exists. Under plain <c>Wrap</c>, <c>C:\Users\…\WindowsApps</c> has one break
/// opportunity, the drive colon, so Avalonia strands <c>C:</c> on the first line and lets the rest overflow.
/// Avalonia 12 has no character-wrap mode, so a zero-width space (U+200B) goes after every character. These are
/// meant to be copied, so the copy strips them back out and the clipboard gets a real, pasteable path.
/// </remarks>
public sealed class CharWrapTextBlock : SelectableTextBlock
{
    const char ZeroWidthSpace = '\u200B';

    /// <summary>The real text. Bind this instead of <see cref="TextBlock.Text"/>.</summary>
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<CharWrapTextBlock, string?>(nameof(Source));

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>
    /// For prose that mentions paths: break only after <c>\</c>, so words wrap as words and paths wrap folder by
    /// folder. Off (the default) breaks anywhere, for a bare path.
    /// </summary>
    public static readonly StyledProperty<bool> ProseProperty =
        AvaloniaProperty.Register<CharWrapTextBlock, bool>(nameof(Prose));

    public bool Prose
    {
        get => GetValue(ProseProperty);
        set => SetValue(ProseProperty, value);
    }

    public CharWrapTextBlock()
    {
        TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        AddHandler(CopyingToClipboardEvent, OnCopyingToClipboard);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == ProseProperty)
            Text = Prose ? Source is null ? null : BreakAfterSeparators(Source) : InsertBreakOpportunities(Source);
    }

    /// <summary>Insert a zero-width space after every character so wrapping can happen anywhere.</summary>
    public static string? InsertBreakOpportunities(string? source)
    {
        if (string.IsNullOrEmpty(source)) return source;
        var sb = new StringBuilder(source.Length * 2);
        foreach (var c in source) sb.Append(c).Append(ZeroWidthSpace);
        return sb.ToString();
    }

    /// <summary>The inverse of <see cref="InsertBreakOpportunities"/>.</summary>
    public static string RemoveBreakOpportunities(string text) => text.Replace(ZeroWidthSpace.ToString(), string.Empty);

    /// <summary>
    /// For prose that mentions paths (finding titles): a break opportunity after each <c>\</c>, so a path wraps
    /// folder by folder instead of at the drive colon. Use it directly only on text that isn't copied; a
    /// <see cref="Prose"/> block strips the breaks from a copy.
    /// </summary>
    public static string BreakAfterSeparators(string text) => text.Replace(@"\", "\\" + ZeroWidthSpace);

    async void OnCopyingToClipboard(object? sender, RoutedEventArgs e)
    {
        // SelectedText carries the zero-width spaces; hand the clipboard the clean text.
        var selected = SelectedText;
        if (string.IsNullOrEmpty(selected)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        e.Handled = true;
        await clipboard.SetTextAsync(RemoveBreakOpportunities(selected));
    }
}
