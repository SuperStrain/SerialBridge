using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialBridge.App.Services;
using SerialBridge.Core.Config;
using SerialBridge.Core.Models;

namespace SerialBridge.App.ViewModels;

/// <summary>映射编辑对话框 ViewModel（编辑的是配置副本，保存时校验并回写）。</summary>
public partial class MappingEditViewModel : ObservableObject
{
    private static readonly int[] CommonBaudRates =
        [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    public ObservableCollection<string> PortNames { get; } = new();
    public ObservableCollection<int> BaudRateOptions { get; } = new(CommonBaudRates);
    public ObservableCollection<int> DataBitsOptions { get; } = new([5, 6, 7, 8]);
    public ObservableCollection<Parity> ParityOptions { get; } = new(
        [Parity.None, Parity.Odd, Parity.Even, Parity.Mark, Parity.Space]);
    public ObservableCollection<StopBits> StopBitsOptions { get; } = new(
        [StopBits.One, StopBits.OnePointFive, StopBits.Two]);
    public ObservableCollection<Handshake> HandshakeOptions { get; } = new(
        [Handshake.None, Handshake.XOnXOff, Handshake.RequestToSend, Handshake.RequestToSendXOnXOff]);
    public ObservableCollection<string> ListenIpOptions { get; } = new();

    [ObservableProperty] private string _title = "新增映射";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClientModeIsSingle))]
    private string _name = "新映射";
    [ObservableProperty] private string _portName = "COM3";
    [ObservableProperty] private string _baudRateText = "115200";
    [ObservableProperty] private int _dataBits = 8;
    [ObservableProperty] private Parity _parity = Parity.None;
    [ObservableProperty] private StopBits _stopBits = StopBits.One;
    [ObservableProperty] private Handshake _flowControl = Handshake.None;
    [ObservableProperty] private string _listenIp = "0.0.0.0";
    [ObservableProperty] private string _listenPortText = "9001";
    [ObservableProperty] private bool _isSingleClient = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClientModeIsSingle))]
    private int _maxClients = 8;
    [ObservableProperty] private bool _autoReconnect = true;
    [ObservableProperty] private string _reconnectSecondsText = "3";
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _hasError;

    public bool ClientModeIsSingle => IsSingleClient;

    public bool Confirmed { get; private set; }

    public MappingEditViewModel(MappingConfig? source)
    {
        RefreshPortList();
        ListenIpOptions.Add("0.0.0.0");
        foreach (var ip in Core.Net.NetworkInfo.GetLocalIPv4Addresses())
            ListenIpOptions.Add(ip.ToString());

        if (source is not null)
        {
            Title = "编辑映射";
            var c = source;
            _name = c.Name;
            _portName = c.PortName;
            _baudRateText = c.BaudRate.ToString();
            _dataBits = c.DataBits;
            _parity = c.Parity;
            _stopBits = c.StopBits;
            _flowControl = c.FlowControl;
            _listenIp = c.ListenIp;
            _listenPortText = c.ListenPort.ToString();
            _isSingleClient = c.ClientMode == ClientMode.Single;
            _maxClients = Math.Max(2, c.MaxClients);
            _autoReconnect = c.AutoReconnect;
            _reconnectSecondsText = (c.ReconnectIntervalMs / 1000.0).ToString("0.#");
            _autoStart = c.AutoStart;
            OnPropertyChanged(nameof(Name));
        }
    }

    /// <summary>解析后的波特率（文本无效时返回 -1，由校验报告）。</summary>
    public int BaudRate =>
        int.TryParse(BaudRateText?.Trim(), out var b) ? b : -1;

    [RelayCommand]
    private void RefreshPortList()
    {
        var names = SerialPortInfo.GetPortNames();
        PortNames.Clear();
        foreach (var n in names) PortNames.Add(n);
        if (PortNames.Count > 0 && !PortNames.Contains(PortName))
            PortName = PortNames[0];
    }

    [RelayCommand]
    private void SetClientModeSingle() => IsSingleClient = true;

    [RelayCommand]
    private void SetClientModeMulti() => IsSingleClient = false;

    /// <summary>构建配置对象；校验失败返回 null 并设置 ErrorMessage。</summary>
    public MappingConfig? Build()
    {
        HasError = false;
        ErrorMessage = "";

        if (string.IsNullOrWhiteSpace(Name))
            return Fail("请输入映射名称。");
        if (!PortNames.Contains(PortName))
            return Fail($"串口 {PortName} 当前不存在，请刷新串口列表后重新选择。");
        if (BaudRate is < 50 or > 4_000_000)
            return Fail("波特率无效（50 – 4,000,000）。");
        if (!int.TryParse(ListenPortText?.Trim(), out var port) || port is < 1 or > 65535)
            return Fail("TCP 端口必须是 1–65535 之间的整数。");
        if (port < 1024)
            return Fail("低于 1024 的端口为系统保留端口，请改用 1024 以上的端口。");
        if (!IsSingleClient && MaxClients is < 2 or > 64)
            return Fail("多客户端模式的最大连接数应在 2 – 64 之间。");
        if (!double.TryParse(ReconnectSecondsText, out var secs) || secs < 0.5 || secs > 60)
            return Fail("自动重连间隔应在 0.5 – 60 秒之间。");

        return new MappingConfig
        {
            Name = Name,
            PortName = PortName,
            BaudRate = BaudRate,
            DataBits = DataBits,
            Parity = Parity,
            StopBits = StopBits,
            FlowControl = FlowControl,
            ListenIp = ListenIp,
            ListenPort = port,
            ClientMode = IsSingleClient ? ClientMode.Single : ClientMode.Multi,
            MaxClients = IsSingleClient ? 1 : MaxClients,
            AutoReconnect = AutoReconnect,
            ReconnectIntervalMs = (int)(secs * 1000),
            AutoStart = AutoStart,
        };
    }

    private MappingConfig? Fail(string message)
    {
        ErrorMessage = message;
        HasError = true;
        return null;
    }

    /// <summary>外部校验失败（如端口被占用）时显示错误。</summary>
    public void FailExternal(string message) => Fail(message);

    public void MarkConfirmed() => Confirmed = true;
}
