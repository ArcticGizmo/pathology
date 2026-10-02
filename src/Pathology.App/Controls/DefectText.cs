using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using static Pathology.App.Theme;

namespace Pathology.App.Controls;

/// <summary>What's wrong with a highlighted stretch of an entry's text.</summary>
public enum TextDefect
{
    None,
    Whitespace,
    Quote,
    DoubledBackslash,
    ForwardSlash,
}

/// <summary>A stretch of an entry's text, and whether it's one of its hygiene defects.</summary>
public sealed record TextSegment(string Text, TextDefect Defect);

/// <summary>
/// Splits an entry's text into plain stretches and the characters the hygiene rules complain about: leading
/// and trailing whitespace, quotes, doubled backslashes (other than a UNC or device prefix) and forward slashes.
/// Spaces at the ends are shown as <c>·</c> so they can be seen at all.
/// </summary>
public static class DefectSegmenter
{
    public static IReadOnlyList<TextSegment> Split(string text)
    {
        var segments = new List<TextSegment>();
        if (text.Length == 0) return segments;

        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        var end = text.Length;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;

        if (start > 0) segments.Add(new(new string('·', start), TextDefect.Whitespace));

        var plain = new StringBuilder();
        // A leading \\ is a UNC (\\server) or device (\\?\) prefix, not a doubled separator.
        var i = start;
        if (end - start >= 2 && text[start] == '\\' && text[start + 1] == '\\')
        {
            plain.Append(@"\\");
            i += 2;
        }
        for (; i < end; i++)
        {
            var c = text[i];
            var defect = c switch
            {
                '"' => TextDefect.Quote,
                '/' => TextDefect.ForwardSlash,
                '\\' when i + 1 < end && text[i + 1] == '\\' => TextDefect.DoubledBackslash,
                _ => TextDefect.None,
            };
            if (defect == TextDefect.None)
            {
                plain.Append(c);
                continue;
            }
            Flush();
            if (defect == TextDefect.DoubledBackslash)
            {
                var run = i;
                while (run < end && text[run] == '\\') run++;
                segments.Add(new(text[i..run], defect));
                i = run - 1;
            }
            else segments.Add(new(c.ToString(), defect));
        }
        Flush();

        if (end < text.Length && end >= start) segments.Add(new(new string('·', text.Length - end), TextDefect.Whitespace));
        return segments;

        void Flush()
        {
            if (plain.Length == 0) return;
            segments.Add(new(plain.ToString(), TextDefect.None));
            plain.Clear();
        }
    }
}

/// <summary>
/// An entry's text with its hygiene defects picked out in the Low colour and underlined. It wraps anywhere,
/// like <see cref="CharWrapTextBlock"/>. Display only: the detail pane has the copyable text.
/// </summary>
public sealed class DefectTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<DefectTextBlock, string?>(nameof(Source));

    /// <summary>Highlight defects (true), or show the text plain.</summary>
    public static readonly StyledProperty<bool> HighlightProperty =
        AvaloniaProperty.Register<DefectTextBlock, bool>(nameof(Highlight), true);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public DefectTextBlock() => TextWrapping = TextWrapping.Wrap;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == HighlightProperty) Build();
    }

    void Build()
    {
        var inlines = new InlineCollection();
        var text = Source ?? "";
        if (text.Trim().Length == 0)
        {
            inlines.Add(new Run(text.Length == 0 ? "(empty)" : $"(empty: {text.Length} space{(text.Length == 1 ? "" : "s")})")
            {
                Foreground = Brush(Theming.NordTheme.LowBrush), FontStyle = FontStyle.Italic,
            });
            Inlines = inlines;
            return;
        }

        var segments = Highlight ? DefectSegmenter.Split(text) : [new TextSegment(text, TextDefect.None)];
        foreach (var segment in segments)
        {
            var run = new Run(CharWrapTextBlock.InsertBreakOpportunities(segment.Text));
            if (segment.Defect != TextDefect.None)
            {
                run.Foreground = Brush(Theming.NordTheme.LowBrush);
                run.FontWeight = FontWeight.Bold;
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
            }
            inlines.Add(run);
        }
        Inlines = inlines;
    }
}
