using System.Windows.Documents;
using SerialBridge.App.Services;
using Xunit;

namespace SerialBridge.App.Tests;

/// <summary>
/// Release 正文 Markdown 渲染回归：标题/列表/强调/链接/行内代码/HTML 兜底,
/// 确保更新弹窗展示渲染后的排版而非源码。
/// </summary>
public sealed class ReleaseNotesRendererTests
{
    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text,
        Span span => string.Concat(span.Inlines.Select(InlineText)),
        _ => "",
    };

    private static IReadOnlyList<string> ParagraphTexts(FlowDocument document) =>
        document.Blocks.OfType<Paragraph>().Select(p => string.Concat(p.Inlines.Select(InlineText))).ToList();

    [Fact]
    public void HeadingAndList_RenderAsStyledBlocks()
    {
        var doc = ReleaseNotesRenderer.ToFlowDocument("### 新增功能\n- 终端命令栏\n- 快捷命令");

        Assert.Equal(3, doc.Blocks.Count);
        var heading = (Paragraph)doc.Blocks.FirstBlock!;
        Assert.True(heading.Inlines.FirstInline is Run r &&
                r.Text == "新增功能" && r.FontWeight == System.Windows.FontWeights.Bold,
            "标题应渲染为加粗段落而非源码");
        Assert.Equal(new[] { "• 终端命令栏", "• 快捷命令" }, ParagraphTexts(doc).Skip(1).ToList());
    }

    [Fact]
    public void BoldInline_RendersAsBoldRunNotSource()
    {
        var doc = ReleaseNotesRenderer.ToFlowDocument("修复 **崩溃** 问题");
        var paragraph = (Paragraph)doc.Blocks.FirstBlock!;

        var boldContainer = Assert.IsType<Bold>(paragraph.Inlines.First(i =>
            i is Bold b && InlineText(b) == "崩溃"));
        // 审查发现的行为回归:子 Run 不得用外层 Normal 覆盖 Bold 容器的字重
        var run = Assert.IsType<Run>(boldContainer.Inlines.FirstInline);
        Assert.Equal(System.Windows.FontWeights.Bold, run.FontWeight);

        Assert.Equal("修复 崩溃 问题", string.Concat(paragraph.Inlines.Select(InlineText)));
        Assert.DoesNotContain("**", string.Concat(paragraph.Inlines.Select(InlineText)));
    }

    [Fact]
    public void Link_RendersAsHyperlinkWithUri()
    {
        var doc = ReleaseNotesRenderer.ToFlowDocument("查看 [发布页](https://github.com/SuperStrain/SerialBridge/releases)。");
        var paragraph = (Paragraph)doc.Blocks.FirstBlock!;

        var hyperlink = Assert.IsType<Hyperlink>(paragraph.Inlines.First(i => i is Hyperlink));
        Assert.Equal("发布页", InlineText(hyperlink));
        Assert.Equal("github.com", hyperlink.NavigateUri!.Host);
    }

    [Fact]
    public void InlineCode_RendersWithMonospaceFont()
    {
        var doc = ReleaseNotesRenderer.ToFlowDocument("运行 `dotnet build` 命令");
        var paragraph = (Paragraph)doc.Blocks.FirstBlock!;

        Assert.Contains(paragraph.Inlines, i =>
            i is Run r && r.Text == "dotnet build" && r.FontFamily!.Source == "Consolas");
    }

    [Fact]
    public void Strikethrough_RendersWithTextDecoration()
    {
        var doc = ReleaseNotesRenderer.ToFlowDocument("~~废弃方案~~ 已移除");
        var paragraph = (Paragraph)doc.Blocks.FirstBlock!;

        Assert.Contains(paragraph.Inlines, i =>
            i is Span s && s.TextDecorations == System.Windows.TextDecorations.Strikethrough);
    }

    [Fact]
    public void HtmlCommentsAndTags_AreStrippedWithoutTypeNames()
    {
        // 审查发现的行为回归:HtmlInline.ToString() 返回类型名,不得插入正文
        var doc = ReleaseNotesRenderer.ToFlowDocument("A<!-- 隐藏注释 -->B");
        var texts = ParagraphTexts(doc);

        Assert.Contains(texts, t => t.Contains("AB"));
        Assert.All(texts, t => Assert.DoesNotContain("<", t));
        Assert.DoesNotContain(texts, t => t.Contains("隐藏注释"));
        Assert.DoesNotContain(texts, t => t.Contains("HtmlInline"));   // 不得把 Markdig 类型名插入正文
    }

    [Fact]
    public void InlineBr_RendersAsLineBreak()
    {
        var doc = ReleaseNotesRenderer.ToFlowDocument("提示<br>请先备份");
        var paragraph = (Paragraph)doc.Blocks.FirstBlock!;

        var text = string.Concat(paragraph.Inlines.Select(InlineText));
        Assert.Equal("提示请先备份", text);
        Assert.Contains(paragraph.Inlines, i => i is LineBreak);
        Assert.DoesNotContain(paragraph.Inlines, i => i is Run r && r.Text.Contains("br"));
    }

    [Fact]
    public void Quote_PreservesListsAndCodeBlocks()
    {
        // 审查发现的行为回归:引用中的列表与代码块不得被丢弃
        var doc = ReleaseNotesRenderer.ToFlowDocument(
            "> 升级提示:\n>\n> - 升级前备份配置\n> - 关闭程序\n>\n> ```\n> setup.exe /SILENT\n> ```");

        var section = Assert.IsType<Section>(doc.Blocks.FirstBlock!);
        var texts = ParagraphTexts(section.Blocks).ToList();

        Assert.Contains(texts, t => t.Contains("升级提示"));
        Assert.Contains(texts, t => t.Contains("• 升级前备份配置"));
        Assert.Contains(texts, t => t.Contains("• 关闭程序"));
        Assert.Contains(texts, t => t.Contains("setup.exe /SILENT"));
    }

    private static IEnumerable<string> ParagraphTexts(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    yield return string.Concat(p.Inlines.Select(InlineText));
                    break;
                case Section s:
                    foreach (var nested in ParagraphTexts(s.Blocks)) yield return nested;
                    break;
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyNotes_YieldEmptyDocument(string? markdown)
    {
        var doc = ReleaseNotesRenderer.ToFlowDocument(markdown);
        Assert.Equal(0, doc.Blocks.Count);
    }

    [Fact]
    public void OrderedAndNestedLists_KeepMarkers()
    {
        var doc = ReleaseNotesRenderer.ToFlowDocument("2. 第二\n3. 第三\n   - 嵌套项");
        var texts = ParagraphTexts(doc);

        Assert.Contains("2. 第二", texts);
        Assert.Contains("3. 第三", texts);
        Assert.Contains("• 嵌套项", texts);
    }
}
