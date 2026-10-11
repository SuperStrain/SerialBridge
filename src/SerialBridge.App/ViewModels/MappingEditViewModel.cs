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

    public ObservableCollection<PortOption> PortOptions { get; } = new();
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClientModeIsSingle))]
    private bool _isSingleClient = true;
    [ObservableProperty] private int _maxClients = 8;
    [ObservableProperty] private bool _dataLogEnabled;
    [ObservableProperty] private string _dataLogDirectory = "";
    [ObservableProperty] private int _dataLogFormatIndex;       // 0=文本 1=HEX
    [ObservableProperty] private int _dataLogEncodingIndex;     // 0=UTF-8 1=GBK（仅文本模式）
    [ObservableProperty] private string _dataLogRetentionDaysText = "30";
    [ObservableProperty] private string _dataLogMaxFileMBText = "50";
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
            _dataLogEnabled = c.DataLog.Enabled;
            _dataLogDirectory = c.DataLog.Directory;
            _dataLogFormatIndex = c.DataLog.Format == DataLogFormat.Hex ? 1 : 0;
            _dataLogEncodingIndex = c.DataLog.TextEncoding == DataLogTextEncoding.Gbk ? 1 : 0;
            _dataLogRetentionDaysText = c.DataLog.RetentionDays.ToString();
            _dataLogMaxFileMBText = c.DataLog.MaxFileMB.ToString();
            OnPropertyChanged(nameof(Name));
        }
    }

    /// <summary>解析后的波特率（文本无效时返回 -1，由校验报告）。</summary>
    public int BaudRate =>
        int.TryParse(BaudRateText?.Trim(), out var b) ? b : -1;

    partial void OnDataLogEnabledChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(DataLogDirectory))
            PickDataLogDirectory();
    }

    [RelayCommand]
    private void PickDataLogDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择串口日志存储目录",
            InitialDirectory = string.IsNullOrWhiteSpace(DataLogDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : DataLogDirectory,
        };
        if (dlg.ShowDialog() == true)
            DataLogDirectory = dlg.FolderName;
    }

    [RelayCommand]
    private void RefreshPortList()
    {
        var options = PortEnumeration.GetPortOptions();
        PortOptions.Clear();
        foreach (var o in options) PortOptions.Add(o);
        if (PortOptions.Count > 0 && PortOptions.All(p => p.Port != PortName))
            PortName = PortOptions[0].Port;
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
        if (PortOptions.All(p => p.Port != PortName))
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
        if (DataLogEnabled && string.IsNullOrWhiteSpace(DataLogDirectory))
            return Fail("已启用串口日志，但未选择存储目录。");
        if (!int.TryParse(DataLogRetentionDaysText, out var retention) || retention is < 7 or > 365)
            return Fail("日志保留天数应在 7 – 365 之间。");
        if (!int.TryParse(DataLogMaxFileMBText, out var maxFile) || maxFile is < 10 or > 500)
            return Fail("单日志文件上限应在 10 – 500 MB 之间。");

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
            DataLog = new DataLogConfig
            {
                Enabled = DataLogEnabled,
                Directory = DataLogDirectory,
                Format = DataLogFormatIndex == 1 ? DataLogFormat.Hex : DataLogFormat.Text,
                TextEncoding = DataLogEncodingIndex == 1 ? DataLogTextEncoding.Gbk : DataLogTextEncoding.Utf8,
                RetentionDays = retention,
                MaxFileMB = maxFile,
            },
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
