using SerialBridge.Core.Update;
using Xunit;

namespace SerialBridge.Core.Tests;

/// <summary>UpdateService 的语义化版本比较与解析测试。</summary>
public class UpdateServiceTests
{
    [Theory]
    [InlineData("1.2.2", "1.3.0", true)]      // minor 更新
    [InlineData("1.2.2", "1.2.3", true)]      // patch 更新
    [InlineData("1.2.2", "2.0.0", true)]      // major 更新
    [InlineData("v1.3.0", "1.4.0", true)]     // v 前缀容忍
    [InlineData("1.2.2", "1.10.0", true)]     // 逐段数值比较（1.10 > 1.9）
    [InlineData("1.2.2", "1.2.2", false)]     // 相同
    [InlineData("1.2.3", "1.2.2", false)]     // 更旧
    [InlineData("1.2.2", "1.2.2-beta", false)]// 预发布尾缀不升级
    public void IsNewerVersion_CoreCases(string current, string candidate, bool expected)
    {
        Assert.Equal(expected, SemanticVersion.IsNewer(current, candidate));
    }

    [Fact]
    public void IsNewerVersion_InvalidInputs_ReturnFalse()
    {
        Assert.False(SemanticVersion.IsNewer("", "1.3.0"));
        Assert.False(SemanticVersion.IsNewer("1.2.2", ""));
        Assert.False(SemanticVersion.IsNewer("abc", "1.3.0"));
        Assert.False(SemanticVersion.IsNewer("1.2", "1.2"));   // 单段/两段非法容错
    }
}
