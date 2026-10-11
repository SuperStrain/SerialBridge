namespace SerialBridge.Core.Models;

public enum DataLogFormat { Text, Hex }
public enum DataLogTextEncoding { Utf8, Gbk }

/// <summary>串口日志（数据落盘）配置。</summary>
public sealed class DataLogConfig
{
    /// <summary>是否启用（启用时必须已选择存储目录）。</summary>
    public bool Enabled { get; set; }

    /// <summary>用户选择的存储目录（开启开关时弹出选择）。</summary>
    public string Directory { get; set; } = "";

    /// <summary>记录格式：Text（Xshell 式正文，仅设备输出）或 Hex（双向带标记）。</summary>
    public DataLogFormat Format { get; set; } = DataLogFormat.Text;

    /// <summary>文本模式的设备字节解码方式：Utf8（原样零损失）或 Gbk（老设备）。</summary>
    public DataLogTextEncoding TextEncoding { get; set; } = DataLogTextEncoding.Utf8;

    /// <summary>日志保留天数（7–365）。</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>单文件大小上限（MB），超限同日分卷。</summary>
    public int MaxFileMB { get; set; } = 50;

    public DataLogConfig Clone() => (DataLogConfig)MemberwiseClone();
}
