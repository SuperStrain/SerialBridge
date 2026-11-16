using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SerialBridge.App.Services;
using SerialBridge.Core.Models;
using SerialBridge.Core.Tcp;

namespace SerialBridge.App.Views;

/// <summary>Separate remote-connection screen with saved connections and opt-in LAN scanning.</summary>
public sealed class RemoteConnectionsWindow : Window
{
    private static RemoteConnectionsWindow? _open;
    private readonly StackPanel _savedList = new();
    private readonly StackPanel _discoveredList = new();
    private readonly TextBox _name = new() { MinWidth = 140 };
    private readonly TextBox _host = new() { MinWidth = 160 };
    private readonly TextBox _port = new() { Width = 90, Text = "9001" };
    private readonly CheckBox _reconnect = new() { Content = "自动重连", Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _autostart = new() { Content = "启动时连接" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Threading.DispatcherTimer _refresh;
    private Guid? _editingId;

    public static void OpenFor(Window owner)
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new RemoteConnectionsWindow { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private RemoteConnectionsWindow()
    {
        Title = "远程 TCP 连接 — SerialBridge";
        Width = 900;
        Height = 680;
        MinWidth = 710;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock
        {
            Text = "远程 TCP 连接", FontSize = 23, FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "连接同一局域网内的 SerialBridge 或其他原始 TCP 串口服务；终端关闭后连接仍会保持。",
            Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap
        });
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

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
        form.Children.Add(new TextBlock
        {
            Text = "连接名称 / 主机地址（IP 或域名） / TCP 端口",
            Margin = new Thickness(0, 0, 0, 5), Foreground = Brushes.DimGray
        });
        var fields = new StackPanel { Orientation = Orientation.Horizontal };
        fields.Children.Add(_name);
        _name.Margin = new Thickness(0, 0, 8, 0);
        fields.Children.Add(_host);
        _host.Margin = new Thickness(0, 0, 8, 0);
        fields.Children.Add(_port);
        form.Children.Add(fields);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
        options.Children.Add(_reconnect);
        options.Children.Add(_autostart);
        form.Children.Add(options);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Action("保存连接", SaveConnection));
        buttons.Children.Add(Action("清空编辑", ClearForm));
        form.Children.Add(buttons);
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
        discoveryHeader.Children.Add(Action("扫描局域网", async () => await ScanAsync()));
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
        Closed += (_, _) => _refresh.Stop();
        ClearForm();
        RefreshSaved();
    }

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

    private void ClearForm()
    {
        _editingId = null;
        _name.Text = "远程设备";
        _host.Text = "192.168.1.100";
        _port.Text = "9001";
        _reconnect.IsChecked = false;
        _autostart.IsChecked = false;
    }

    private async void SaveConnection()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_host.Text) ||
            !int.TryParse(_port.Text, out var port) || port is < 1 or > 65535)
        {
            _status.Text = "请输入有效的名称、服务器地址和 TCP 端口（1～65535）。";
            return;
        }
        var existing = AppServices.Remotes.Sessions.FirstOrDefault(s => s.Config.Id == _editingId);
        try
        {
            if (existing is not null)
            {
                // Cancel and await any pending connect/retry BEFORE changing host/port.
                // This also disconnects an active connection; user reconnects after edit.
                await AppServices.Remotes.UpdateConfigurationAsync(existing, _name.Text,
                    _host.Text, port, _reconnect.IsChecked == true,
                    _autostart.IsChecked == true);
                _status.Text = "连接配置已更新，原连接已断开，请重新连接。";
            }
            else
            {
                AppServices.Remotes.Add(new RemoteConnectionConfig
                {
                    Name = _name.Text.Trim(), Host = _host.Text.Trim(), Port = port,
                    AutoReconnect = _reconnect.IsChecked == true,
                    AutoConnectOnStartup = _autostart.IsChecked == true
                });
                _status.Text = "远程连接已保存。";
            }
            AppServices.SaveSettingsSafe();
            ClearForm();
            RefreshSaved();
        }
        catch (Exception ex) { _status.Text = "保存失败：" + ex.Message; }
    }

    private void RefreshSaved()
    {
        _savedList.Children.Clear();
        var sessions = AppServices.Remotes.Sessions;
        if (sessions.Count == 0)
        {
            _savedList.Children.Add(new TextBlock { Text = "暂无连接。可手动添加或从局域网扫描结果中保存。" });
            return;
        }
        foreach (var session in sessions)
        {
            var item = new Border
            {
                BorderThickness = new Thickness(1), BorderBrush = Brushes.LightGray,
                CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 9),
                Padding = new Thickness(12)
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = session.Config.Name, FontWeight = FontWeights.SemiBold, FontSize = 15 });
            stack.Children.Add(new TextBlock
            {
                Text = $"{session.Config.Host}:{session.Config.Port} · {(session.IsConnected ? "已连接" : "未连接")} · " +
                       $"发送 {session.SentBytes:N0} B / 接收 {session.ReceivedBytes:N0} B",
                Foreground = Brushes.DimGray
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(Action(session.IsConnected ? "断开" : "连接", async () =>
            {
                try
                {
                    if (session.IsConnected) await AppServices.Remotes.DisconnectAsync(session);
                    else await AppServices.Remotes.ConnectAsync(session);
                    _status.Text = session.IsConnected ? "连接成功。" : "连接已断开。";
                }
                catch (Exception ex) { _status.Text = "连接失败：" + ex.Message; }
                RefreshSaved();
            }));
            actions.Children.Add(Action("终端", () =>
            {
                if (!session.IsConnected)
                {
                    _status.Text = "请先连接远程设备。";
                    return;
                }
                TerminalWindow.OpenFor(session);
            }));
            actions.Children.Add(Action("编辑", () =>
            {
                _editingId = session.Config.Id;
                _name.Text = session.Config.Name;
                _host.Text = session.Config.Host;
                _port.Text = session.Config.Port.ToString();
                _reconnect.IsChecked = session.Config.AutoReconnect;
                _autostart.IsChecked = session.Config.AutoConnectOnStartup;
            }));
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
            _savedList.Children.Add(item);
        }
    }

    private async Task ScanAsync()
    {
        _status.Text = "正在扫描局域网（约 2 秒）…";
        _discoveredList.Children.Clear();
        try
        {
            var devices = await LanDiscoveryScanner.ScanAsync(TimeSpan.FromSeconds(2));
            _status.Text = devices.Count == 0
                ? "未发现设备。请确认对方允许被发现、映射正在监听且防火墙未拦截 UDP 39001。"
                : $"发现 {devices.Count} 台设备。";
            foreach (var device in devices)
            {
                var group = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
                group.Children.Add(new TextBlock
                {
                    Text = $"{device.Name}  ·  {device.Address}",
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
                            TerminalWindow.OpenFor(existing);
                            _status.Text = "已连接并打开终端。";
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
