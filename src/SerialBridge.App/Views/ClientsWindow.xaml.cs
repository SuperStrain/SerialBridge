using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Net;
using SerialBridge.App.Services;
using SerialBridge.Core.Models;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
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
        {
            _viewModel.ShowHistory = ClientTabs.SelectedIndex == 1;
            _viewModel.ShowBlacklist = ClientTabs.SelectedIndex == 2;
            if (_viewModel.ShowBlacklist) _viewModel.RefreshBlacklist();
        }
    }

    public ClientsWindow(MappingRuntime runtime)
    {
        InitializeComponent();
        _viewModel = new ClientsViewModel(runtime);
        DataContext = _viewModel;
        Closed += (_, _) => { _viewModel.Stop(); OpenWindows.Remove(runtime.Config.Id); };
        _viewModel.RefreshBlacklist();
    }
    private void OnDisconnectClient(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not ClientRow row) return;
        if (MessageBox.Show(this, $"确定断开 {row.Remote} 吗？该 IP 可重新连接。",
            "确认断开客户端", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!_viewModel.DisconnectClient(row.Id))
            MessageBox.Show(this, "该连接已经断开。", "客户端详情", MessageBoxButton.OK, MessageBoxImage.Information);
        _viewModel.RefreshNow();
    }

    private void OnBlockCurrent(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not ClientRow row) return;
        var ip = row.RemoteIp;
        if (MessageBox.Show(this,
            $"将 {ip} 加入全局黑名单？本机所有映射中来自此 IP 的连接都会立即断开，之后的连接也将被拒绝。\n共享该出口 IP 的其他设备也会受到影响。",
            "确认加入黑名单", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ApplyBlock(ip, "在线连接加入");
    }

    private void OnAddBlockedIp(object sender, RoutedEventArgs e)
    {
        ApplyBlock(BlockIpInput.Text.Trim(), "手动添加");
    }

    private void ApplyBlock(string ip, string source)
    {
        try
        {
            if (!AppServices.BlockIp(ip, BlockRemarkInput.Text, source))
                MessageBox.Show(this, "该 IP 已在黑名单中。", "黑名单管理", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(this, $"已加入黑名单：{ip}。匹配的在线连接已断开。", "黑名单管理", MessageBoxButton.OK, MessageBoxImage.Information);
            _viewModel.RefreshBlacklist();
            _viewModel.RefreshNow();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "黑名单操作失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnRemoveBlockedIp(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not BlockedIpEntry entry) return;
        if (MessageBox.Show(this, $"移除黑名单 IP {entry.IpAddress}？移除后允许新连接，但不会恢复已断开会话。",
            "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            AppServices.UnblockIp(entry.IpAddress);
            _viewModel.RefreshBlacklist();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "移除失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnEditBlockedRemark(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not BlockedIpEntry entry) return;
        var dialog = new Window
        {
            Title = $"编辑备注 — {entry.IpAddress}", Owner = this, Width = 410,
            Height = 165, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        var input = new System.Windows.Controls.TextBox { Text = entry.Remark, Margin = new Thickness(0, 0, 0, 12) };
        var save = new System.Windows.Controls.Button { Content = "保存", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        panel.Children.Add(input); panel.Children.Add(save); dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        try
        {
            AppServices.UpdateBlockedIpRemark(entry.IpAddress, input.Text);
            _viewModel.RefreshBlacklist();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存备注失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

}

/// <summary>一行客户端快照。</summary>
public sealed class ClientRow
{
    public Guid Id { get; init; }
    public string RemoteIp { get; init; } = "";
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
    public ObservableCollection<BlockedIpEntry> BlockedIps { get; } = new();
    public bool ShowBlacklist { get; set; }
    public void RefreshBlacklist()
    {
        var entries = AppServices.Blacklist.Entries;
        if (BlockedIps.Count == entries.Count &&
            BlockedIps.Zip(entries).All(pair =>
                pair.First.IpAddress == pair.Second.IpAddress &&
                pair.First.Remark == pair.Second.Remark &&
                pair.First.Source == pair.Second.Source &&
                pair.First.AddedAtUtc == pair.Second.AddedAtUtc))
            return;
        BlockedIps.Clear();
        foreach (var entry in entries) BlockedIps.Add(entry);
    }
    public bool DisconnectClient(Guid id) => _runtime.Tcp.DisconnectClient(id);
    public void RefreshNow() => Refresh();

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
        _timer.Tick += (_, _) =>
        {
            Refresh();
            if (ShowBlacklist) RefreshBlacklist();
        };
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
                Id = c.Id,
                RemoteIp = c.RemoteIp.ToString(),
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
