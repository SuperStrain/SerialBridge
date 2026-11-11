using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using System.Windows.Controls;
using SerialBridge.App.ViewModels;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.History;
using Wpf.Ui.Controls;

namespace SerialBridge.App.Views;

public partial class ClientsWindow : FluentWindow
{
    private static readonly Dictionary<Guid, ClientsWindow> OpenWindows = new();

    private readonly ClientsViewModel _viewModel;

    /// <summary>打开（或激活）指定映射的客户端详情窗口。</summary>
    public static void OpenFor(MappingRuntime runtime)
    {
        if (OpenWindows.TryGetValue(runtime.Config.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new ClientsWindow(runtime);
        OpenWindows[runtime.Config.Id] = window;
        window.Show();
    }

    private void OnClientTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, ClientTabs)) return;
        if (_viewModel is not null)
            _viewModel.ShowHistory = ClientTabs.SelectedIndex == 1;
    }

    public ClientsWindow(MappingRuntime runtime)
    {
        InitializeComponent();
        _viewModel = new ClientsViewModel(runtime);
        DataContext = _viewModel;
        Closed += (_, _) => { _viewModel.Stop(); OpenWindows.Remove(runtime.Config.Id); };
    }
}

/// <summary>一行客户端快照。</summary>
public sealed class ClientRow
{
    public string Remote { get; init; } = "";
    public string ConnectedAtText { get; init; } = "";
    public string DurationText { get; init; } = "";
    public string SentText { get; init; } = "";
    public string RecvText { get; init; } = "";
}

/// <summary>客户端详情 ViewModel：每秒刷新快照（连接列表 + 收发计数）。</summary>
public sealed class ClientsViewModel : INotifyPropertyChanged
{
    private readonly MappingRuntime _runtime;
    private readonly DispatcherTimer _timer;

    public string Title { get; }
    public ObservableCollection<ClientRow> Clients { get; } = new();

    private int _clientCount;
    private long _totalAccepted;
    private bool _showHistory;
    private bool _loadingHistory;
    private string _historyStatus = "切换到历史连接可加载已保存的会话。";
    public ObservableCollection<HistoryRow> History { get; } = new();
    public bool ShowHistory
    {
        get => _showHistory;
        set
        {
            if (_showHistory == value) return;
            _showHistory = value;
            PropertyChanged?.Invoke(this, new(nameof(ShowHistory)));
            PropertyChanged?.Invoke(this, new(nameof(ShowOnline)));
            if (value) _ = LoadHistoryAsync();
        }
    }
    public bool ShowOnline => !ShowHistory;
    public string HistoryStatus
    {
        get => _historyStatus;
        private set { _historyStatus = value; PropertyChanged?.Invoke(this, new(nameof(HistoryStatus))); }
    }
    public System.Windows.Input.ICommand RefreshHistoryCommand { get; }


    public int ClientCount { get => _clientCount; private set { _clientCount = value; PropertyChanged?.Invoke(this, new(nameof(ClientCount))); } }
    public long TotalAccepted { get => _totalAccepted; private set { _totalAccepted = value; PropertyChanged?.Invoke(this, new(nameof(TotalAccepted))); } }
    public bool HasClients => ClientCount > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ClientsViewModel(MappingRuntime runtime)
    {
        _runtime = runtime;
        RefreshHistoryCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(LoadHistoryAsync);
        Title = $"客户端详情 — {runtime.Config.Name}";
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var conns = _runtime.Tcp.Clients
            .OrderBy(c => c.ConnectedAtUtc)
            .ToList();
        ClientCount = conns.Count;
        TotalAccepted = _runtime.TotalAcceptedClients;

        Clients.Clear();
        foreach (var c in conns)
        {
            var connectedFor = DateTime.UtcNow - c.ConnectedAtUtc;
            Clients.Add(new ClientRow
            {
                Remote = c.RemoteEndPoint,
                ConnectedAtText = c.ConnectedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
                DurationText = connectedFor.TotalHours >= 1
                    ? $"{(int)connectedFor.TotalHours}时{connectedFor.Minutes}分"
                    : $"{connectedFor.Minutes}分{connectedFor.Seconds}秒",
                SentText = MappingViewModel.FormatBytes(c.SentBytes),
                RecvText = MappingViewModel.FormatBytes(c.RecvBytes),
            });
        }
        PropertyChanged?.Invoke(this, new(nameof(HasClients)));
    }

    private async Task LoadHistoryAsync()
    {
        if (_loadingHistory) return;
        _loadingHistory = true;
        HistoryStatus = "正在读取持久化历史…";
        try
        {
            if (ClientHistoryStore.Shared is not { } store)
            {
                HistoryStatus = "历史数据库不可用，请检查软件运行日志。";
                return;
            }
            var items = await store.GetHistoryAsync(_runtime.Config.Id);
            History.Clear();
            foreach (var item in items)
                History.Add(new HistoryRow
                {
                    Remote = item.RemoteAddress,
                    ConnectedAtText = item.ConnectedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    DisconnectedAtText = item.DisconnectedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知（异常中断/在线）",
                    DurationText = item.DurationMs is long ms ? TimeSpan.FromMilliseconds(ms).ToString(@"dd\.hh\:mm\:ss") : "未知",
                    SentText = MappingViewModel.FormatBytes(item.SentBytes),
                    RecvText = MappingViewModel.FormatBytes(item.ReceivedBytes),
                    Reason = item.DisconnectReason ?? "在线",
                });
            HistoryStatus = store.LastWriteError is { } error
                ? "警告：部分连接历史写入失败：" + error
                : $"历史记录 {History.Count} 条 · 每条映射最多保存 1000 条已结束会话 · 手动刷新";
        }
        catch (Exception ex)
        {
            HistoryStatus = "读取历史失败：" + ex.Message;
        }
        finally { _loadingHistory = false; }
    }

    public void Stop() => _timer.Stop();
}

/// <summary>One persisted client session, independent of active clients.</summary>
public sealed class HistoryRow
{
    public string Remote { get; init; } = "";
    public string ConnectedAtText { get; init; } = "";
    public string DisconnectedAtText { get; init; } = "";
    public string DurationText { get; init; } = "";
    public string SentText { get; init; } = "";
    public string RecvText { get; init; } = "";
    public string Reason { get; init; } = "";
}
