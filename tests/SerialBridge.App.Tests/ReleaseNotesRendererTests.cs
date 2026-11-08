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

        Assert.Contains(paragraph.Inlines, i =>
            i is Bold b && InlineText(b) == "崩溃");
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
    public void HtmlCommentsAndTags_AreStripped()
    {
        var doc = ReleaseNotesRenderer.ToFlowDocument("A<!-- 隐藏注释 -->B\n\n<details><summary>x</summary>y</details>");
        var texts = ParagraphTexts(doc);

        Assert.All(texts, t => Assert.DoesNotContain("<", t));
        Assert.DoesNotContain(texts, t => t.Contains("隐藏注释"));
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
