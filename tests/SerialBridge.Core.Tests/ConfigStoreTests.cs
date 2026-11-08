using SerialBridge.Core.Config;
using Xunit;

namespace SerialBridge.Core.Tests;

/// <summary>
/// 配置加载净化测试：命令步骤的格式/换行必须归一化到编码器可接受的规范值，
/// 避免手改配置或历史数据在发送时报出晦涩的 FormatException。
/// </summary>
public sealed class ConfigStoreTests
{
    private static string WriteTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sb-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_NormalizesStepFormatAndEndingValues()
    {
        var path = WriteTempConfig("""
            {
              "Version": 1,
              "Mappings": [],
              "General": { "LogRetentionDays": 30 },
              "CommandPresets": [
                { "Name": "hex小写", "Steps": [ { "Format": "hex", "Content": "01 03", "Ending": "cr", "DelayMs": 0 } ] },
                { "Name": "lf小写", "Steps": [ { "Format": "Text", "Content": "ls", "Ending": "lf", "DelayMs": 0 } ] },
                { "Name": "未知值", "Steps": [ { "Format": "bogus", "Content": "x", "Ending": "weird", "DelayMs": 0 } ] },
                { "Name": "空换行", "Steps": [ { "Format": "text", "Content": "x", "Ending": "", "DelayMs": 0 } ] },
                { "Name": "null换行", "Steps": [ { "Format": "Text", "Content": "x", "Ending": null, "DelayMs": 0 } ] }
              ]
            }
            """);
        try
        {
            var settings = new ConfigStore(path).Load();
            var steps = settings.CommandPresets.SelectMany(p => p.Steps).ToList();

            // HEX 强制 None；大小写归一；未知回退 CR（串口回车标准）；空串与 null 等价 None（编码器原语义）
            Assert.Equal(("Hex", "None"), (steps[0].Format, steps[0].Ending));
            Assert.Equal(("Text", "LF"), (steps[1].Format, steps[1].Ending));
            Assert.Equal(("Text", "CR"), (steps[2].Format, steps[2].Ending));
            Assert.Equal(("Text", "None"), (steps[3].Format, steps[3].Ending));
            Assert.Equal(("Text", "None"), (steps[4].Format, steps[4].Ending));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingShowCommandBarDefaultsToVisible()
    {
        var path = WriteTempConfig("""
            { "Version": 1, "Mappings": [], "General": { "LogRetentionDays": 30 } }
            """);
        try
        {
            var settings = new ConfigStore(path).Load();
            Assert.True(settings.General.ShowCommandBar);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
