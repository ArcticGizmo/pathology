using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using static Pathology.App.Theme;

namespace Pathology.App.Controls;

/// <summary>
/// Renders markdown as real controls: headings, paragraphs, lists, quotes, code blocks, tables and inline
/// emphasis and code. A trimmed port of perch's <c>MarkdownView</c>, painted from the palette. Text stays
/// selectable. Links render as link-coloured text but aren't followed: the Learn articles are self-contained.
/// </summary>
public sealed class MarkdownBlock : ContentControl
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();
    static readonly FontFamily Mono = new("Cascadia Code, Consolas, monospace");

    const double BodySize = 13.5;
    const double BlockGap = 12;

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownBlock, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty) Content = Build(Markdown ?? "");
    }

    /// <summary>The control tree for a markdown document. A parse failure falls back to the raw text.</summary>
    public static Control Build(string md)
    {
        var root = new StackPanel();
        if (string.IsNullOrWhiteSpace(md)) return root;

        MarkdownDocument doc;
        try { doc = Markdig.Markdown.Parse(md, Pipeline); }
        catch { root.Children.Add(Paragraph(md)); return root; }

        foreach (var block in doc)
            if (RenderBlock(block, Brush("FgBrush")) is { } c)
                root.Children.Add(c);
        return root;
    }

    static Control? RenderBlock(Block block, IBrush fg) => block switch
    {
        HeadingBlock h => Heading(h),
        ParagraphBlock p => Paragraph(p, fg),
        ListBlock list => List(list, fg),
        QuoteBlock q => Quote(q),
        Table table => TableView(table),
        CodeBlock code => Code(code),
        ThematicBreakBlock => new Border { Height = 1, Background = Brush("BorderBrush"), Margin = new Thickness(0, 6, 0, 14) },
        ContainerBlock cb => Stack(cb, fg),
        _ => null,
    };

    static Control Heading(HeadingBlock h)
    {
        var size = h.Level switch { 1 => 20, 2 => 16.5, 3 => 14.5, _ => 13.5 };
        var text = new SelectableTextBlock
        {
            FontSize = size, FontWeight = FontWeight.SemiBold, Foreground = Brush("TitleBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, h.Level <= 2 ? 10 : 6, 0, 8),
        };
        if (h.Inline is { } inline) text.Inlines = Inlines(inline, new Style(size, Brush("TitleBrush"), Bold: true));
        return text;
    }

    static SelectableTextBlock Paragraph(ParagraphBlock p, IBrush fg)
    {
        var tb = Paragraph("");
        tb.Foreground = fg;
        if (p.Inline is { } inline) tb.Inlines = Inlines(inline, new Style(BodySize, fg));
        return tb;
    }

    static SelectableTextBlock Paragraph(string text) => new()
    {
        Text = text, Foreground = Brush("FgBrush"), FontSize = BodySize, TextWrapping = TextWrapping.Wrap,
        LineHeight = BodySize * 1.55, Margin = new Thickness(0, 0, 0, BlockGap),
    };

    static Control List(ListBlock list, IBrush fg)
    {
        var panel = new StackPanel { Margin = new Thickness(2, 0, 0, BlockGap), Spacing = 4 };
        var number = list.OrderedStart is { } start && int.TryParse(start, out var n) ? n : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var content = new StackPanel();
            foreach (var child in item)
                if (RenderBlock(child, fg) is { } c)
                {
                    if (c is SelectableTextBlock stb) stb.Margin = new Thickness(0);
                    content.Children.Add(c);
                }

            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}." : "•", Foreground = Brush("MutedBrush"), FontSize = BodySize,
                MinWidth = list.IsOrdered ? 20 : 12, Margin = new Thickness(0, 0, 8, 0),
                TextAlignment = list.IsOrdered ? TextAlignment.Right : TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    static Control Quote(QuoteBlock q)
    {
        var inner = new StackPanel();
        foreach (var child in q)
            if (RenderBlock(child, Brush("MutedBrush")) is { } c)
            {
                if (c is SelectableTextBlock stb) stb.Margin = new Thickness(0, 0, 0, 4);
                inner.Children.Add(c);
            }
        return new Border
        {
            BorderBrush = Brush("AccentBrush"), BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 0, 0, BlockGap), Child = inner,
        };
    }

    static Control Code(CodeBlock code) => new Border
    {
        Background = Brush("FormBgBrush"), CornerRadius = new CornerRadius(6),
        BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1),
        Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, BlockGap),
        Child = new SelectableTextBlock
        {
            Text = code.Lines.ToString().Replace("\r", "").TrimEnd('\n'), FontFamily = Mono, FontSize = 12.5,
            Foreground = Brush("FgBrush"), TextWrapping = TextWrapping.Wrap,
        },
    };

    static Control Stack(ContainerBlock cb, IBrush fg)
    {
        var panel = new StackPanel();
        foreach (var child in cb)
            if (RenderBlock(child, fg) is { } c)
                panel.Children.Add(c);
        return panel;
    }

    static Control TableView(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        if (rows.Count == 0) return new StackPanel();
        var cols = rows.Max(r => r.Count);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", cols))) };
        for (var r = 0; r < rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var r = 0; r < rows.Count; r++)
        {
            var ci = 0;
            foreach (var cell in rows[r].OfType<TableCell>())
            {
                var header = rows[r].IsHeader;
                var tb = new SelectableTextBlock
                {
                    FontSize = BodySize - 0.5, Foreground = Brush("FgBrush"), TextWrapping = TextWrapping.Wrap,
                    FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                };
                var inlines = new InlineCollection();
                foreach (var leaf in cell.OfType<LeafBlock>())
                    if (leaf.Inline is { } inline)
                        foreach (var i in Inlines(inline, new Style(BodySize - 0.5, Brush("FgBrush"), Bold: header)).ToList())
                            inlines.Add(i);
                tb.Inlines = inlines;

                var border = new Border
                {
                    BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = header ? Brush("ButtonBgBrush") : null, Padding = new Thickness(10, 6), Child = tb,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, ci++);
                grid.Children.Add(border);
            }
        }
        return new Border
        {
            BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 0, 0, BlockGap), HorizontalAlignment = HorizontalAlignment.Left, Child = grid,
        };
    }

    readonly record struct Style(double Size, IBrush Brush, bool Bold = false, bool Italic = false, bool Link = false);

    static InlineCollection Inlines(ContainerInline container, Style style)
    {
        var sink = new InlineCollection();
        Append(sink, container, style);
        return sink;
    }

    static void Append(InlineCollection sink, ContainerInline container, Style style)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sink.Add(Styled(lit.Content.ToString(), style));
                    break;
                case CodeInline code:
                    sink.Add(new Run(code.Content)
                    {
                        FontFamily = Mono, FontSize = style.Size - 0.5, Foreground = Brush("AccentBrush"),
                    });
                    break;
                case EmphasisInline em:
                    Append(sink, em, em.DelimiterCount >= 2 ? style with { Bold = true } : style with { Italic = true });
                    break;
                case LinkInline link:
                    Append(sink, link, style with { Brush = Brush("LinkBrush"), Link = true });
                    break;
                case LineBreakInline br:
                    sink.Add(new Run(br.IsHard ? "\n" : " ") { Foreground = style.Brush });
                    break;
                case ContainerInline cc:
                    Append(sink, cc, style);
                    break;
            }
        }
    }

    static Run Styled(string text, Style style)
    {
        var run = new Run(text)
        {
            Foreground = style.Brush, FontSize = style.Size,
            FontWeight = style.Bold ? FontWeight.SemiBold : FontWeight.Normal,
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
        };
        if (style.Link) run.TextDecorations = TextDecorations.Underline;
        return run;
    }
}
