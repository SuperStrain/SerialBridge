using System.Windows;
using System.Windows.Controls;
using SerialBridge.Core.Models;

namespace SerialBridge.App.Views;

/// <summary>新增/编辑远程连接的对话框：由远程连接页按钮唤起，替代页内常驻表单。</summary>
public sealed class RemoteConnectionEditWindow : Window
{
    private readonly TextBox _name = new() { MinWidth = 220 };
    private readonly TextBox _host = new() { MinWidth = 220 };
    private readonly TextBox _port = new() { Width = 120, Text = "9001" };
    private readonly CheckBox _reconnect = new() { Content = "自动重连" };
    private readonly CheckBox _autostart = new() { Content = "启动时连接" };

    /// <summary>点击"保存"且校验通过后返回配置；取消/关闭返回 null。</summary>
    public RemoteConnectionConfig? Result { get; private set; }

    public RemoteConnectionEditWindow(Window owner, RemoteConnectionConfig? initial)
    {
        Title = initial is null ? "新增远程连接" : "编辑远程连接";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.White;
        Owner = owner;

        var root = new StackPanel { Margin = new Thickness(22) };
        Content = root;

        root.Children.Add(new TextBlock
        {
            Text = "连接名称", Margin = new Thickness(0, 0, 0, 5)
        });
        root.Children.Add(_name);
        root.Children.Add(new TextBlock
        {
            Text = "主机地址（IP 或域名）", Margin = new Thickness(0, 12, 0, 5)
        });
        root.Children.Add(_host);
        root.Children.Add(new TextBlock
        {
            Text = "TCP 端口", Margin = new Thickness(0, 12, 0, 5)
        });
        root.Children.Add(_port);

        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        _reconnect.Margin = new Thickness(0, 0, 16, 0);
        options.Children.Add(_reconnect);
        options.Children.Add(_autostart);
        root.Children.Add(options);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var save = new Button { Content = "保存", Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        save.Click += (_, _) => Save();
        var cancel = new Button
        {
            Content = "取消", Padding = new Thickness(18, 6, 18, 6),
            Margin = new Thickness(10, 0, 0, 0), IsCancel = true
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        if (initial is not null)
        {
            _name.Text = initial.Name;
            _host.Text = initial.Host;
            _port.Text = initial.Port.ToString();
            _reconnect.IsChecked = initial.AutoReconnect;
            _autostart.IsChecked = initial.AutoConnectOnStartup;
        }
        else
        {
            _name.Text = "远程设备";
            _host.Text = "192.168.1.100";
        }

        _name.Focus();
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_host.Text) ||
            !int.TryParse(_port.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("请输入有效的名称、服务器地址和 TCP 端口（1～65535）。",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = new RemoteConnectionConfig
        {
            Name = _name.Text.Trim(),
            Host = _host.Text.Trim(),
            Port = port,
            AutoReconnect = _reconnect.IsChecked == true,
            AutoConnectOnStartup = _autostart.IsChecked == true
        };
        DialogResult = true;
    }
}
