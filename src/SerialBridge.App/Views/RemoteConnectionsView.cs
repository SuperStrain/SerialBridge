using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SerialBridge.App.Services;
using SerialBridge.Core.Models;
using SerialBridge.Core.Tcp;

namespace SerialBridge.App.Views;

/// <summary>主窗口内嵌的远程连接页面：已保存连接与可选的局域网扫描。</summary>
public sealed class RemoteConnectionsView : UserControl
{
    private readonly StackPanel _savedList = new();
    private readonly StackPanel _discoveredList = new();
    private readonly ComboBox _discoveryMode = new()
    {
        Width = 125,
        ItemsSource = new[] { "本机网段", "指定网段" },
        SelectedIndex = 0
    };
    private readonly TextBox _discoveryTarget = new()
    {
        MinWidth = 180, MaxWidth = 230,
        Text = "192.168.2.0/24",
        ToolTip = "输入目标 IPv4 网段，如 192.168.2.0/24（最多扫描 254 个地址）",
        IsEnabled = false
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Threading.DispatcherTimer _refresh;

    // Reuse row controls across timer ticks so scrolling, keyboard focus and
    // in-progress button interactions are never destroyed by a status refresh.
    private readonly Dictionary<Guid, SavedConnectionRow> _savedRows = new();
    // Only a success notice from this session may be replaced by its later EOF.
    // An unrelated connection completing later must retain its own status.
    private RemoteTcpSession? _successNoticeSession;

    private sealed record SavedConnectionRow(
        Border Container, TextBlock Name, TextBlock Details, Button ConnectionButton);

    public RemoteConnectionsView()
    {
        var root = new DockPanel();
        Content = root;

        _status.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var tabs = new TabControl();
        root.Children.Add(tabs);

        var savedTab = new TabItem { Header = "已保存连接" };
        tabs.Items.Add(savedTab);
        var savedRoot = new DockPanel { Margin = new Thickness(12) };
        savedTab.Content = savedRoot;
        var form = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(form, Dock.Top);
        savedRoot.Children.Add(form);
        form.Children.Add(Action("新增连接", () => OpenEditDialog(null)));
        var savedScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        savedScroll.Content = _savedList;
        savedRoot.Children.Add(savedScroll);

        var discoveryTab = new TabItem { Header = "扫描局域网" };
        tabs.Items.Add(discoveryTab);
        var discoveryRoot = new DockPanel { Margin = new Thickness(12) };
        discoveryTab.Content = discoveryRoot;
        var discoveryHeader = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        discoveryHeader.Children.Add(new TextBlock
        {
            Text = "仅显示主动启用“允许被发现”的设备及正在监听的 COM 映射。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var discoveryControls = new StackPanel { Orientation = Orientation.Horizontal };
        discoveryControls.Children.Add(Action("扫描局域网", async () => await ScanAsync()));
        discoveryControls.Children.Add(_discoveryMode);
        discoveryControls.Children.Add(new TextBlock
        {
            Text = "目标网段",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        });
        discoveryControls.Children.Add(_discoveryTarget);
        _discoveryMode.SelectionChanged += (_, _) =>
            _discoveryTarget.IsEnabled = _discoveryMode.SelectedIndex == 1;
        discoveryHeader.Children.Add(discoveryControls);
        discoveryHeader.Children.Add(new TextBlock
        {
            Text = "本机网段使用广播；指定网段使用限速 UDP 单播（/24～/32），需目标网段可路由且防火墙放行 UDP 39001。",
            Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        DockPanel.SetDock(discoveryHeader, Dock.Top);
        discoveryRoot.Children.Add(discoveryHeader);
        discoveryRoot.Children.Add(new ScrollViewer
        {
            Content = _discoveredList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        _discoveredList.Children.Add(new TextBlock { Text = "点击扫描以发现附近的 SerialBridge。" });

        _refresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) => RefreshSaved();
        _refresh.Start();
        RefreshSaved();
    }

    /// <summary>宿主窗口真正关闭时停止状态刷新计时器。</summary>
    public void StopRefresh() => _refresh.Stop();

    private static Button Action(string label, Action handler)
    {
        var button = new Button
        {
            Content = label, Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0), MinWidth = 76
        };
        button.Click += (_, _) => handler();
        return button;
    }

    /// <summary>弹出新增/编辑对话框；编辑时保持原连接 Id 并重载配置。</summary>
    private async void OpenEditDialog(SerialBridge.Core.Tcp.RemoteTcpSession? session)
    {
        var owner = Window.GetWindow(this);
        var dialog = new RemoteConnectionEditWindow(owner, session?.Config);
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        var cfg = dialog.Result;
        try
        {
            if (session is not null)
            {
                // Cancel and await any pending connect/retry BEFORE changing host/port.
                // This also disconnects an active connection; user reconnects after edit.
                await AppServices.Remotes.UpdateConfigurationAsync(session, cfg.Name,
                    cfg.Host, cfg.Port, cfg.AutoReconnect, cfg.AutoConnectOnStartup);
                _status.Text = "连接配置已更新，原连接已断开，请重新连接。";
            }
            else
            {
                AppServices.Remotes.Add(cfg);
                _status.Text = "远程连接已保存。";
            }
            AppServices.SaveSettingsSafe();
            RefreshSaved();
        }
        catch (Exception ex) { _status.Text = "保存失败：" + ex.Message; }
    }

    private void RefreshSaved()
    {
        var sessions = AppServices.Remotes.Sessions;
        var ids = sessions.Select(s => s.Config.Id).ToHashSet();
        // Only rebuild the list when its membership or order changes.
        // The one-second timer otherwise updates the existing controls in place.
        if (_savedRows.Count != sessions.Count ||
            _savedRows.Keys.Any(id => !ids.Contains(id)) ||
            !sessions.Select(s => s.Config.Id).SequenceEqual(
                _savedList.Children.OfType<Border>()
                    .Select(border => _savedRows.First(pair => pair.Value.Container == border).Key)))
        {
            _savedList.Children.Clear();
            _savedRows.Clear();
            if (sessions.Count == 0)
            {
                _savedList.Children.Add(new TextBlock
                {
                    Text = "暂无连接。可手动添加或从局域网扫描结果中保存。"
                });
            }
            foreach (var session in sessions)
            {
                var row = CreateSavedRow(session);
                _savedRows.Add(session.Config.Id, row);
                _savedList.Children.Add(row.Container);
            }
        }

        foreach (var session in sessions)
        {
            var row = _savedRows[session.Config.Id];
            row.Name.Text = session.Config.Name;
            row.Details.Text = $"{session.Config.Host}:{session.Config.Port} · " +
                $"{(session.IsConnected ? "已连接" : "未连接")} · " +
                $"发送 {session.SentBytes:N0} B / 接收 {session.ReceivedBytes:N0} B";
            row.ConnectionButton.Content = session.IsConnected ? "断开" : "连接";
            if (!session.IsConnected && session.WasQuickDisconnect)
                row.Details.Text += " · " + session.LastDisconnectReason;
        }
        // Correct only the notice produced by THIS session. Another connection
        // may have completed later and written an unrelated success message.
        if (_successNoticeSession is { IsConnected: false, WasQuickDisconnect: true } recent &&
            (_status.Text == "连接成功。" || _status.Text == "已连接并打开终端。"))
        {
            _status.Text = recent.LastDisconnectReason;
            _successNoticeSession = null;
        }
    }

    private void ShowConnectResult(RemoteTcpSession session, string successMessage)
    {
        _successNoticeSession = session.IsConnected ? session : null;
        _status.Text = session.IsConnected
            ? successMessage
            : session.WasQuickDisconnect
                ? session.LastDisconnectReason
                : "连接已断开。";
    }

    private SavedConnectionRow CreateSavedRow(SerialBridge.Core.Tcp.RemoteTcpSession session)
    {
        var item = new Border
        {
            BorderThickness = new Thickness(1), BorderBrush = Brushes.LightGray,
            CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 9),
            Padding = new Thickness(12)
        };
        var stack = new StackPanel();
        var name = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 15 };
        var details = new TextBlock { Foreground = Brushes.DimGray };
        stack.Children.Add(name);
        stack.Children.Add(details);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0)
        };
        var connectionButton = Action("连接", async () =>
        {
            try
            {
                if (session.IsConnected) await AppServices.Remotes.DisconnectAsync(session);
                else await AppServices.Remotes.ConnectAsync(session);
                ShowConnectResult(session, "连接成功。");
            }
            catch (Exception ex) { _status.Text = "连接失败：" + ex.Message; }
            RefreshSaved();
        });
        actions.Children.Add(connectionButton);
        actions.Children.Add(Action("终端", () =>
        {
            if (!session.IsConnected)
            {
                _status.Text = "请先连接远程设备。";
                return;
            }
            TerminalWindow.OpenFor(session);
        }));
        actions.Children.Add(Action("编辑", () => OpenEditDialog(session)));
        actions.Children.Add(Action("删除", async () =>
        {
            try
            {
                await AppServices.Remotes.RemoveAsync(session);
                AppServices.SaveSettingsSafe();
                _status.Text = "连接配置已删除。";
                RefreshSaved();
            }
            catch (Exception ex) { _status.Text = "删除失败：" + ex.Message; }
        }));
        stack.Children.Add(actions);
        item.Child = stack;
        return new SavedConnectionRow(item, name, details, connectionButton);
    }

    private async Task ScanAsync()
    {
        string? subnet = null;
        if (_discoveryMode.SelectedIndex == 1)
        {
            subnet = _discoveryTarget.Text.Trim();
            try { LanDiscoveryScanner.ExpandCidr(subnet); }
            catch (ArgumentException ex)
            {
                _status.Text = ex.Message;
                return;
            }
        }
        _status.Text = "正在扫描局域网（约 3 秒）…";
        _discoveredList.Children.Clear();
        try
        {
            var devices = await LanDiscoveryScanner.ScanAsync(TimeSpan.FromSeconds(3),
                targetSubnet: subnet);
            _status.Text = devices.Count == 0
                ? "未发现设备。请确认对方允许被发现、映射正在监听且防火墙未拦截 UDP 39001。"
                : $"发现 {devices.Count} 台设备。";
            foreach (var device in devices)
            {
                var group = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
                var isLocal = device.DeviceId == AppServices.Settings.DiscoveryDeviceId;
                group.Children.Add(new TextBlock
                {
                    Text = $"{device.Name}{(isLocal ? "（本机）" : "")}  ·  {device.Address}",
                    FontSize = 16, FontWeight = FontWeights.SemiBold
                });
                foreach (var mapping in device.Mappings)
                {
                    var connectIp = mapping.ConnectAddress(device.Address);
                    var row = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Margin = new Thickness(12, 5, 0, 0)
                    };
                    row.Children.Add(new TextBlock
                    {
                        Text = $"{mapping.Name} · {mapping.ComPort} · {connectIp}:{mapping.TcpPort} · {mapping.SerialParameters}",
                        MinWidth = 450, VerticalAlignment = VerticalAlignment.Center
                    });
                    row.Children.Add(Action("保存并连接", async () =>
                    {
                        var existing = AppServices.Remotes.Sessions.FirstOrDefault(s =>
                            s.Config.Host == connectIp && s.Config.Port == mapping.TcpPort);
                        if (existing is null)
                        {
                            existing = AppServices.Remotes.Add(new RemoteConnectionConfig
                            {
                                Name = $"{device.Name} - {mapping.ComPort}",
                                Host = connectIp, Port = mapping.TcpPort,
                            });
                            AppServices.SaveSettingsSafe();
                        }
                        try
                        {
                            await AppServices.Remotes.ConnectAsync(existing);
                            if (existing.IsConnected) TerminalWindow.OpenFor(existing);
                            ShowConnectResult(existing, "已连接并打开终端。");
                        }
                        catch (Exception ex) { _status.Text = "连接失败：" + ex.Message; }
                        RefreshSaved();
                    }));
                    group.Children.Add(row);
                }
                _discoveredList.Children.Add(group);
            }
        }
        catch (Exception ex) { _status.Text = "扫描失败：" + ex.Message; }
    }
}
