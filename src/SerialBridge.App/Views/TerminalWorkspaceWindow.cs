using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Models;

namespace SerialBridge.App.Views;

/// <summary>
/// One UI workspace, one independent WebView2/xterm per mapping. Never opens another serial port.
/// Closing a tab only detaches its UI subscriptions; the mapping and TCP listener keep running.
/// </summary>
public sealed class TerminalWorkspaceWindow : Wpf.Ui.Controls.FluentWindow
{
    private static TerminalWorkspaceWindow? _instance;
    private static readonly object EnvironmentLock = new();
    private static Task<CoreWebView2Environment>? _environmentTask;

    private readonly TabControl _tabs = new();
    private readonly Dictionary<Guid, Session> _sessions = new();
    private bool _closing;

    private sealed class Session
    {
        public required MappingRuntime Runtime { get; init; }
        public required TabItem Tab { get; init; }
        public required WebView2 Web { get; init; }
        public required TextBlock Status { get; init; }
        public Action<MonitorEntry>? OnData;
        public Action<MappingRuntime>? OnStatus;
        public EventHandler<CoreWebView2WebMessageReceivedEventArgs>? OnMessage;
        public EventHandler<CoreWebView2NavigationCompletedEventArgs>? OnNavigation;
        public bool Ready;
        public bool Closed;
    }

    public static void OpenFor(MappingRuntime runtime)
    {
        if (_instance is null || _instance._closing)
        {
            _instance = new TerminalWorkspaceWindow();
            _instance.Show();
        }
        _instance.OpenTab(runtime);
        if (_instance.WindowState == WindowState.Minimized)
            _instance.WindowState = WindowState.Normal;
        _instance.Activate();
    }

    private TerminalWorkspaceWindow()
    {
        Title = "SerialBridge — 多标签串口终端";
        Width = 1060;
        Height = 740;
        MinWidth = 760;
        MinHeight = 480;
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 46));
        _tabs.Background = Background;
        _tabs.SelectionChanged += (_, _) => FocusActiveSession();
        var root = new DockPanel();
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 6, 12, 6)
        };
        var clear = new Button
        {
            Content = "清屏",
            ToolTip = "只清除当前标签的本地终端显示，不向设备发送任何命令",
            Padding = new Thickness(12, 5, 12, 5)
        };
        clear.Click += (_, _) => ClearActiveTab();
        toolbar.Children.Add(clear);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(_tabs);
        Content = root;
        PreviewKeyDown += OnShortcutKeyDown;
        Closed += (_, _) =>
        {
            _closing = true;
            foreach (var session in _sessions.Values.ToArray()) RemoveTab(session);
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
    }

    private void OnShortcutKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
            (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
        if (e.Key == Key.Tab)
        {
            CycleTab((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.W)
        {
            CloseActiveTab();
            e.Handled = true;
        }
    }

    private void CycleTab(int delta)
    {
        if (_tabs.Items.Count < 2) return;
        var index = _tabs.SelectedIndex;
        _tabs.SelectedIndex = (index + delta + _tabs.Items.Count) % _tabs.Items.Count;
        FocusActiveSession();
    }

    private void ClearActiveTab()
    {
        if (_tabs.SelectedItem is TabItem tab && tab.Tag is Session session &&
            session.Ready && !session.Closed)
            _ = session.Web.ExecuteScriptAsync("window.clearTerm()");
    }

    private void CloseActiveTab()
    {
        if (_tabs.SelectedItem is TabItem tab &&
            tab.Tag is Session session) RemoveTab(session);
    }

    private void FocusActiveSession()
    {
        if (_tabs.SelectedItem is TabItem tab && tab.Tag is Session session &&
            session.Ready && !session.Closed)
        {
            session.Web.Focus();
            _ = session.Web.ExecuteScriptAsync("window.focusTerm()");
        }
    }

    private void OpenTab(MappingRuntime runtime)
    {
        if (_sessions.TryGetValue(runtime.Config.Id, out var existing))
        {
            _tabs.SelectedItem = existing.Tab;
            FocusActiveSession();
            return;
        }

        var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 46) };
        var status = new TextBlock { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = $"{runtime.Config.Name} ({runtime.Config.PortName})",
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 0), ToolTip = "关闭标签（不停止串口映射）" };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(status);
        header.Children.Add(name);
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = web };
        var session = new Session { Runtime = runtime, Tab = tab, Web = web, Status = status };
        tab.Tag = session;
        close.Click += (_, _) => RemoveTab(session);

        session.OnStatus = _ => Dispatcher.BeginInvoke(new Action(() => UpdateStatus(session)));
        session.OnData = entry =>
        {
            if (entry.Direction != DataDirection.SerialToNet || session.Closed) return;
            var b64 = Convert.ToBase64String(entry.Data);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!session.Closed && session.Ready)
                    _ = session.Web.ExecuteScriptAsync($"window.writeB64('{b64}')");
            }));
        };
        runtime.StatusChanged += session.OnStatus;
        runtime.DataTransferred += session.OnData;

        _sessions.Add(runtime.Config.Id, session);
        _tabs.Items.Add(tab);
        _tabs.SelectedItem = tab;
        UpdateStatus(session);
        _ = InitializeSessionAsync(session);
    }

    private void UpdateStatus(Session session)
    {
        if (session.Closed) return;
        var status = session.Runtime.GetStatus();
        session.Status.Text = status.State == MappingState.Stopped ? "○" :
            status.State == MappingState.Faulted || status.SerialState == SerialState.Faulted ? "✕" :
            status.SerialState == SerialState.Open ? "●" : "◐";
        session.Status.Foreground = status.SerialState == SerialState.Open && status.State == MappingState.Running
            ? Brushes.LightGreen : status.State == MappingState.Faulted || status.SerialState == SerialState.Faulted
            ? Brushes.IndianRed : Brushes.Goldenrod;
        session.Tab.ToolTip = $"映射：{session.Runtime.Config.Name}\n串口：{session.Runtime.Config.PortName}\n映射状态：{status.State}\n串口状态：{status.SerialState}" +
            (string.IsNullOrWhiteSpace(status.SerialMessage) ? "" : $"\n{status.SerialMessage}");
    }

    private static Task<CoreWebView2Environment> GetEnvironment()
    {
        lock (EnvironmentLock)
        {
            // A failed/cancelled environment must not poison every later tab.
            if (_environmentTask is null || _environmentTask.IsFaulted || _environmentTask.IsCanceled)
            {
                var task = CoreWebView2Environment.CreateAsync(
                    null,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SerialBridge", "WebView2"));
                _environmentTask = task;
            }
            return _environmentTask;
        }
    }

    private async Task InitializeSessionAsync(Session session)
    {
        try
        {
            var environment = await GetEnvironment();
            if (session.Closed) return;
            await session.Web.EnsureCoreWebView2Async(environment);
            if (session.Closed) return;
            var core = session.Web.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.SetVirtualHostNameToFolderMapping("terminal.serialbridge.local",
                Path.Combine(AppContext.BaseDirectory, "Assets", "terminal"),
                CoreWebView2HostResourceAccessKind.Allow);
            session.OnMessage = (_, args) => OnWebMessage(session, args);
            session.OnNavigation = (_, args) =>
            {
                if (session.Closed || !args.IsSuccess) return;
                session.Ready = true;
                if (ReferenceEquals(_tabs.SelectedItem, session.Tab)) FocusActiveSession();
            };
            core.WebMessageReceived += session.OnMessage;
            core.NavigationCompleted += session.OnNavigation;
            session.Web.Source = new Uri("https://terminal.serialbridge.local/index.html");
        }
        catch (Exception ex)
        {
            if (!session.Closed)
                session.Status.ToolTip = "终端初始化失败：" + ex.Message;
        }
    }

    private async void OnWebMessage(Session session, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (session.Closed || _closing) return;
        try
        {
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            var data = json.RootElement;
            var type = data.GetProperty("type").GetString();
            if (type == "workspace-shortcut")
            {
                if (!ReferenceEquals(_tabs.SelectedItem, session.Tab)) return;
                switch (data.GetProperty("action").GetString())
                {
                    case "next": CycleTab(1); break;
                    case "previous": CycleTab(-1); break;
                    case "close": CloseActiveTab(); break;
                }
                return;
            }

            if (!session.Ready) return;
            var isActive = ReferenceEquals(_tabs.SelectedItem, session.Tab);
            if (type == "input")
            {
                var text = data.GetProperty("text").GetString();
                if (text is null) return;
                // xterm emits some protocol replies through onData as well as user
                // keypresses. Background tabs must answer DA/DSR requests, while
                // ordinary keyboard sequences from an inactive tab stay blocked.
                if (isActive || IsTerminalProtocolReply(text))
                    session.Runtime.WriteLocal(Encoding.UTF8.GetBytes(text));
            }
            else if (!isActive) return;
            else if (type == "copy")
            {
                var text = data.GetProperty("text").GetString();
                if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
            }
            else if (type == "paste" && Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                await session.Web.ExecuteScriptAsync($"window.pasteText({JsonSerializer.Serialize(text)})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("workspace terminal message: " + ex.Message);
        }
    }

    private static bool IsTerminalProtocolReply(string text)
    {
        // Terminal-generated Device Status Reports: ESC[0n / ESC[3n,
        // Cursor Position Report: ESC[row;colR,
        // Primary / secondary Device Attributes: ESC[?params c, ESC[>params c.
        // Do not allow arbitrary escape/control data from inactive tabs.
        return System.Text.RegularExpressions.Regex.IsMatch(
            text, @"^\x1b\[(?:[0-9]{1,3}n|[0-9]{1,4};[0-9]{1,4}R|[?>][0-9;]{0,32}c)$");
    }

    private void RemoveTab(Session session)
    {
        if (session.Closed) return;
        session.Closed = true;
        if (session.OnStatus is not null) session.Runtime.StatusChanged -= session.OnStatus;
        if (session.OnData is not null) session.Runtime.DataTransferred -= session.OnData;
        if (session.Web.CoreWebView2 is { } core)
        {
            if (session.OnMessage is not null) core.WebMessageReceived -= session.OnMessage;
            if (session.OnNavigation is not null) core.NavigationCompleted -= session.OnNavigation;
        }
        _sessions.Remove(session.Runtime.Config.Id);
        _tabs.Items.Remove(session.Tab);
        session.Web.Dispose();
        if (_tabs.Items.Count == 0 && !_closing) Close();
    }
}
