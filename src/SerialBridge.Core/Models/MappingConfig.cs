using System.IO.Ports;

namespace SerialBridge.Core.Models;

/// <summary>一条“物理串口 ↔ TCP 监听端口”映射的持久化配置。</summary>
public sealed class MappingConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private string _name = "新映射";
    public string Name
    {
        get => _name;
        set => _name = string.IsNullOrWhiteSpace(value) ? "新映射" : value.Trim();
    }

    public string PortName { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake FlowControl { get; set; } = Handshake.None;

    /// <summary>TCP 监听地址，"0.0.0.0" 表示所有网卡。</summary>
    public string ListenIp { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = 9001;

    public ClientMode ClientMode { get; set; } = ClientMode.Single;

    /// <summary>多客户端模式下的最大连接数（含单客户端模式恒为 1）。</summary>
    public int MaxClients { get; set; } = 1;

    public bool AutoReconnect { get; set; } = true;

    /// <summary>串口自动重连间隔（毫秒）。</summary>
    public int ReconnectIntervalMs { get; set; } = 3000;

    /// <summary>软件启动时自动启动该映射。</summary>
    public bool AutoStart { get; set; }

    /// <summary>串口日志（数据落盘）配置。</summary>
    public DataLogConfig DataLog { get; set; } = new();

    /// <summary>深拷贝，用于编辑对话框取消时不影响原配置。</summary>
    public MappingConfig Clone() => (MappingConfig)MemberwiseClone();

    /// <summary>用另一份配置的值覆盖自身（保留 Id 不变，除非源为空）。</summary>
    public void CopyFrom(MappingConfig src)
    {
        Name = src.Name;
        PortName = src.PortName;
        BaudRate = src.BaudRate;
        DataBits = src.DataBits;
        Parity = src.Parity;
        StopBits = src.StopBits;
        FlowControl = src.FlowControl;
        ListenIp = src.ListenIp;
        ListenPort = src.ListenPort;
        ClientMode = src.ClientMode;
        MaxClients = src.MaxClients;
        AutoReconnect = src.AutoReconnect;
        ReconnectIntervalMs = src.ReconnectIntervalMs;
        AutoStart = src.AutoStart;
        DataLog = src.DataLog.Clone();
    }

    public string SerialSummary => $"{PortName} @ {BaudRate} {DataBits}{ParityToChar()}{(int)StopBits}";

    private string ParityToChar() => Parity switch
    {
        Parity.None => "N",
        Parity.Even => "E",
        Parity.Odd => "O",
        Parity.Mark => "M",
        Parity.Space => "S",
        _ => "N",
    };
}
