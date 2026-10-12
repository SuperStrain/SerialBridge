using System.Text.Json;
using SerialBridge.Core.Update;
using Xunit;

namespace SerialBridge.Core.Tests;

/// <summary>Release 解析测试：版本号选最大、draft/prerelease 过滤、重定向 tag 提取。</summary>
public class ReleaseParsingTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void TrySelectHighestRelease_PublishOrderReversed_StillPicksHighestVersion()
    {
        // 复现 v1.3.3 事故：低版本后发布（数组顺序在前），必须仍选出 v1.3.4
        var root = Parse("""
            [
              {"tag_name":"v1.3.3","draft":false,"prerelease":false},
              {"tag_name":"v1.3.4","draft":false,"prerelease":false}
            ]
            """);
        Assert.True(ReleaseParsing.TrySelectHighestRelease(root, out _, out var tag));
        Assert.Equal("v1.3.4", tag);
    }

    [Fact]
    public void TrySelectHighestRelease_NumericCompareNotLexical()
    {
        var root = Parse("""
            [
              {"tag_name":"v1.9.0","draft":false,"prerelease":false},
              {"tag_name":"v1.10.0","draft":false,"prerelease":false}
            ]
            """);
        Assert.True(ReleaseParsing.TrySelectHighestRelease(root, out _, out var tag));
        Assert.Equal("v1.10.0", tag);
    }

    [Fact]
    public void TrySelectHighestRelease_SkipsDraftAndPrerelease()
    {
        var root = Parse("""
            [
              {"tag_name":"v2.0.0","draft":true,"prerelease":false},
              {"tag_name":"v1.11.0","draft":false,"prerelease":true},
              {"tag_name":"v1.4.0","draft":false,"prerelease":false}
            ]
            """);
        Assert.True(ReleaseParsing.TrySelectHighestRelease(root, out _, out var tag));
        Assert.Equal("v1.4.0", tag);
    }

    [Fact]
    public void TrySelectHighestRelease_AllFilteredOut_ReturnsFalse()
    {
        var root = Parse("""[{"tag_name":"v2.0.0","draft":true,"prerelease":false}]""");
        Assert.False(ReleaseParsing.TrySelectHighestRelease(root, out _, out _));
    }

    [Fact]
    public void TrySelectHighestRelease_EmptyArrayOrInvalidTags_ReturnsFalse()
    {
        Assert.False(ReleaseParsing.TrySelectHighestRelease(Parse("[]"), out _, out _));
        Assert.False(ReleaseParsing.TrySelectHighestRelease(
            Parse("""[{"tag_name":"not-a-version"}]"""), out _, out _));
        Assert.False(ReleaseParsing.TrySelectHighestRelease(
            Parse("""{"message":"Not Found"}"""), out _, out _));
    }

    [Fact]
    public void TrySelectHighestRelease_SingleObjectMode_WorksAndRespectsFlags()
    {
        var root = Parse("""{"tag_name":"v1.3.4","draft":false,"prerelease":false}""");
        Assert.True(ReleaseParsing.TrySelectHighestRelease(root, out _, out var tag));
        Assert.Equal("v1.3.4", tag);

        Assert.False(ReleaseParsing.TrySelectHighestRelease(
            Parse("""{"tag_name":"v2.0.0","draft":false,"prerelease":true}"""), out _, out _));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/releases/tag/v1.3.4", "v1.3.4")]
    [InlineData("/owner/repo/releases/tag/v1.2.3", "v1.2.3")]                       // 相对地址
    [InlineData("https://github.com/o/r/releases/tag/v1.3.4?query=1#frag", "v1.3.4")] // 带 query/fragment
    public void TryParseTagFromRedirect_ValidLocation_ExtractsTag(string location, string expected)
    {
        Assert.True(ReleaseParsing.TryParseTagFromRedirect(location, out var tag));
        Assert.Equal(expected, tag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://github.com/owner/repo/releases")]
    [InlineData("https://github.com/owner/repo/releases/tag/")]   // 空 tag
    public void TryParseTagFromRedirect_Invalid_ReturnsFalse(string location)
    {
        Assert.False(ReleaseParsing.TryParseTagFromRedirect(location, out _));
    }
}
