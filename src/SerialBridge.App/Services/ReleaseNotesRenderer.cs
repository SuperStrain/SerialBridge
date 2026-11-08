using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig;
using Markdig.Extensions.TaskLists;
using MdBlock = Markdig.Syntax.Block;

namespace SerialBridge.App.Services;

/// <summary>
/// Release 正文(Markdown)→ WPF FlowDocument 渲染器,用于更新弹窗。
/// 覆盖发布说明常见结构:标题、列表(含嵌套/有序/任务)、粗斜体、行内代码、
/// 链接与自动链接、引用、代码块、HTML 兜底(剥离标签保文字)。
/// 配色取当前应用主题资源(弹窗为模态,生命周期内主题不会切换,静态取色安全)。
/// </summary>
public static class ReleaseNotesRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .Build();

    private static readonly Regex HtmlComment = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<int, double> HeadingSizes =
        new Dictionary<int, double> { [1] = 20, [2] = 18, [3] = 16, [4] = 14.5, [5] = 13.5, [6] = 13.5 };

    public static FlowDocument ToFlowDocument(string? markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Foreground = Brush("TextFillColorPrimaryBrush", SystemColors.WindowTextBrush),
            FontSize = 13,
        };

        if (string.IsNullOrWhiteSpace(markdown)) return document;

        var parsed = Markdown.Parse(markdown, Pipeline);
        foreach (var block in parsed)
            AddBlock(document.Blocks, block, depth: 0, bullet: null);
        return document;
    }

    private static Brush Brush(string key, Brush fallback) =>
        (Application.Current?.TryFindResource(key) as Brush) ?? fallback;

    private static Brush SecondaryBrush => Brush("TextFillColorSecondaryBrush", SystemColors.GrayTextBrush);

    private static Brush AccentBrush => Brush("AccentBrush", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));

    private static Brush CodeBackground => new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80));

    private static Brush QuoteBorder => new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    // ---------- 块级 ----------

    private static void AddBlock(BlockCollection target, MdBlock block, int depth, string? bullet)
    {
        switch (block)
        {
            case HeadingBlock heading:
                target.Add(BuildHeading(heading));
                break;
            case ParagraphBlock paragraph:
                target.Add(BuildParagraph(paragraph.Inline, depth, bullet, bottomMargin: bullet is null ? 6 : 2));
                break;
            case ListBlock list:
                AddList(target, list, depth);
                break;
            case QuoteBlock quote:
                target.Add(BuildQuote(quote, depth));
                break;
            case FencedCodeBlock or CodeBlock:
                target.Add(BuildCodeBlock(block));
                break;
            case ThematicBreakBlock:
                break;
            case HtmlBlock html:
                var htmlText = StripHtml(html.Lines.ToString());
                if (htmlText.Length > 0) target.Add(BuildDimParagraph(htmlText, depth));
                break;
            default:
                // 其他罕见块:输出其文字内容,保证信息不丢
                var literal = StripHtml(block.ToString());
                if (literal.Length > 0) target.Add(BuildDimParagraph(literal, depth));
                break;
        }
    }

    private static Paragraph BuildHeading(HeadingBlock heading)
    {
        var size = HeadingSizes.TryGetValue(heading.Level, out var s) ? s : 14.5;
        var paragraph = new Paragraph { Margin = new Thickness(0, heading.Level <= 2 ? 10 : 8, 0, 6) };
        AddInline(paragraph.Inlines, heading.Inline,
            heading.Level <= 4 ? FontWeights.Bold : FontWeights.Normal, null, size);
        return paragraph;
    }

    private static Paragraph BuildParagraph(ContainerInline inlines, int depth, string? bullet, double bottomMargin)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(depth * 14 + (bullet is null ? 0 : 16), 0, 0, bottomMargin),
        };
        if (bullet is not null)
            paragraph.Inlines.Add(new Run(bullet) { Foreground = SecondaryBrush });
        AddInline(paragraph.Inlines, inlines, FontWeights.Normal, null, 13);
        return paragraph;
    }

    private static void AddList(BlockCollection target, ListBlock list, int depth)
    {
        var index = ParseOrderedStart(list);
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var marker = list.IsOrdered ? $"{index}. " : "• ";
            var first = true;
            foreach (var child in item)
            {
                switch (child)
                {
                    case ListBlock nested:
                        AddList(target, nested, depth + 1);
                        break;
                    case ParagraphBlock p:
                        target.Add(BuildParagraph(p.Inline, depth, first ? marker : null, bottomMargin: 2));
                        break;
                    default:
                        AddBlock(target, child, depth + 1, first ? marker : null);
                        break;
                }
                first = false;
            }
            index++;
        }
    }

    private static int ParseOrderedStart(ListBlock list)
    {
        if (!list.IsOrdered) return 1;
        return int.TryParse(list.OrderedStart, out var start) ? start : 1;
    }

    private static Section BuildQuote(QuoteBlock quote, int depth)
    {
        // 引用渲染为左侧竖线的缩进容器:内部子块(段落/列表/代码/嵌套引用)递归复用块级渲染,
        // 避免只处理段落导致引用中的列表与代码块内容被丢弃。
        var section = new Section
        {
            Margin = new Thickness(depth * 14 + 6, 2, 0, 6),
            Padding = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = QuoteBorder,
        };
        foreach (var child in quote)
            AddBlock(section.Blocks, child, depth, bullet: null);
        return section;
    }

    private static Paragraph BuildCodeBlock(MdBlock block)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 8),
            Background = CodeBackground,
            Padding = new Thickness(8, 6, 8, 6),
        };
        var text = block switch
        {
            FencedCodeBlock fenced => fenced.Lines.ToString(),
            CodeBlock code => code.Lines.ToString(),
            _ => block.ToString(),
        };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(lines[i].TrimEnd()) { FontFamily = new FontFamily("Consolas"), FontSize = 12 });
        }
        return paragraph;
    }

    private static Paragraph BuildDimParagraph(string text, int depth)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(depth * 14, 0, 0, 6),
            Foreground = SecondaryBrush,
        };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(lines[i]));
        }
        return paragraph;
    }

    // ---------- 行内 ----------

    private static void AddInline(
        InlineCollection target, ContainerInline container,
        FontWeight weight, FontStyle? style, double size)
    {
        foreach (var inline in container)
            AddInline(target, inline, weight, style, size);
    }

    private static void AddInline(
        InlineCollection target, Markdig.Syntax.MarkdownObject inline,
        FontWeight weight, FontStyle? style, double size)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(FormatRun(new Run(literal.Content.ToString()), weight, style, size));
                break;
            case CodeInline code:
                var codeRun = FormatRun(new Run(code.Content), weight, style, Math.Max(12, size - 1));
                codeRun.FontFamily = new FontFamily("Consolas");
                codeRun.Background = CodeBackground;
                target.Add(codeRun);
                break;
            case EmphasisInline emphasis:
                // 强调 = 语义容器:子内联自动继承加粗/斜体/删除线样式
                System.Windows.Documents.Span emphasisContainer = emphasis.DelimiterChar is '*' or '_'
                    ? (emphasis.DelimiterCount >= 2 ? new Bold() : new Italic())
                    : new Span { TextDecorations = System.Windows.TextDecorations.Strikethrough };
                target.Add(emphasisContainer);
                AddInline(emphasisContainer.Inlines, emphasis, weight, style, size);
                break;
            case LinkInline link when link.IsImage:
                var alt = link.FirstChild is LiteralInline li ? li.Content.ToString() : "[图片]";
                target.Add(FormatRun(new Run("[" + alt + "]"), weight, style, size));
                break;
            case LinkInline link:
                var hyperlink = NewHyperlink(link.Url);
                AddInline(hyperlink.Inlines, link, weight, style, size);
                target.Add(hyperlink);
                break;
            case AutolinkInline autolink:
                target.Add(NewHyperlink(autolink.Url, autolink.Url));
                break;
            case TaskList task:
                target.Add(new Run(task.Checked ? "[x] " : "[ ] ") { Foreground = SecondaryBrush });
                break;
            case LineBreakInline:
                target.Add(new LineBreak());
                break;
            case HtmlInline htmlInline:
                var htmlText = StripHtml(htmlInline.ToString());
                if (htmlText.Length > 0)
                    target.Add(FormatRun(new Run(htmlText), weight, style, size));
                break;
            default:
                var fallback = StripHtml(inline?.ToString() ?? "");
                if (fallback.Length > 0)
                    target.Add(FormatRun(new Run(fallback), weight, style, size));
                break;
        }
    }

    private static Hyperlink NewHyperlink(string? url, string? text = null)
    {
        var hyperlink = new Hyperlink();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) hyperlink.NavigateUri = uri;
        hyperlink.Foreground = AccentBrush;
        hyperlink.TextDecorations = System.Windows.TextDecorations.Underline;
        hyperlink.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* 打开浏览器失败不打扰用户 */ }
            e.Handled = true;
        };
        if (text is not null) hyperlink.Inlines.Add(new Run(text));
        return hyperlink;
    }

    private static Run FormatRun(Run run, FontWeight weight, FontStyle? style, double size)
    {
        run.FontWeight = weight;
        if (style is not null) run.FontStyle = style.Value;
        run.FontSize = size;
        return run;
    }

    private static string StripHtml(string source) =>
        HtmlTag.Replace(HtmlComment.Replace(source ?? "", ""), "").Trim();
}
