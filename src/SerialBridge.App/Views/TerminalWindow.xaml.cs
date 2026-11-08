using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using SerialBridge.App.Services;
using SerialBridge.App.ViewModels;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Models;

namespace SerialBridge.App.Views;

/// <summary>
/// 串口终端（Xshell 式哑终端）：WebView2 + xterm.js 渲染设备输出，
/// 用户按键（onData）逐字节透传 MappingRuntime.WriteLocal 到设备。
/// 本地不回显——与真实串口一致，所见即设备回显。
/// </summary>
public partial class TerminalWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly Dictionary<Guid, TerminalWindow> OpenWindows = new();

    private static readonly SolidColorBrush CommandBarMuted = FrozenBrush("#9399B2");
    private static readonly SolidColorBrush CommandBarError = FrozenBrush("#F38BA8");
    private const string EmptyCommandBarHint = "暂无快捷命令，点击「编辑」创建";

    private readonly TerminalViewModel _viewModel;
    private readonly MappingRuntime _runtime;
    private readonly TerminalWindowLifetime _lifetime = new();
    private CancellationTokenSource? _commandBarCancellation;
    private Task? _initializationTask;
    private bool _ready;

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>打开（或激活）指定映射的串口终端窗口。</summary>
    public static void OpenFor(MappingRuntime runtime)
    {
        if (OpenWindows.TryGetValue(runtime.Config.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new TerminalWindow(runtime);
        OpenWindows[runtime.Config.Id] = window;
        window.Show();
    }

    public TerminalWindow(MappingRuntime runtime)
    {
        InitializeComponent();
        _runtime = runtime;
        _viewModel = new TerminalViewModel(runtime);
        DataContext = _viewModel;

        Loaded += (_, _) =>
            _initializationTask ??= InitializeWebViewAsync(_lifetime.Token);
        Closed += (_, _) =>
        {
            _viewModel.Detach();
            _runtime.DataTransferred -= OnDeviceData;
            CommandPaletteWindow.PresetsSaved -= OnPresetsSaved;
            // 关闭终端即中止命令栏宏：窗口没了，后台不能再向设备继续发送剩余步骤。
            _commandBarCancellation?.Cancel();
            OpenWindows.Remove(runtime.Config.Id);
            _ = CleanupWebViewAsync();
        };
        _runtime.DataTransferred += OnDeviceData;
        CommandPaletteWindow.PresetsSaved += OnPresetsSaved;

        // 命令栏显隐持久化（全局），打开时按上次状态恢复
        CommandBarHost.Visibility = AppServices.Settings.General.ShowCommandBar
            ? Visibility.Visible : Visibility.Collapsed;
        if (CommandBarHost.Visibility == Visibility.Visible) RebuildCommandBar();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // The HWND starts transparent so Windows never presents its default light surface.
        // At this point WPF has already painted the dark placeholder.
        Opacity = 1;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        _lifetime.BeginClose();
        TerminalWindowPresentation.HideForClose(
            isCancelled: false,
            () => Web.Visibility = Visibility.Hidden,
            () =>
            {
                Opacity = 0;
                Hide();
            });
    }

    private async Task InitializeWebViewAsync(CancellationToken cancellationToken)
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SerialBridge", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            cancellationToken.ThrowIfCancellationRequested();
            await Web.EnsureCoreWebView2Async(env);
            cancellationToken.ThrowIfCancellationRequested();

            // 终端接管按键，避免 Ctrl+Shift+C 打开 DevTools、Ctrl+R 刷新页面等。
            Web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // 本地资源映射（免 HTTP 服务、离线可用）
            var terminalDir = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "terminal.serialbridge.local", terminalDir,
                CoreWebView2HostResourceAccessKind.Allow);

            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            cancellationToken.ThrowIfCancellationRequested();
            Web.Source = new Uri("https://terminal.serialbridge.local/index.html");
        }
        catch (OperationCanceledException) when (_lifetime.IsClosing)
        {
            // The user closed the terminal while WebView2 was still initializing.
        }
        catch (Exception) when (_lifetime.IsClosing)
        {
            // Closing/disposal can make an in-flight WebView2 operation fail; stay silent.
        }
        catch (Exception ex)
        {
            TerminalLoadingText.Text = "终端组件初始化失败。";
            MessageBox.Show(
                "终端组件初始化失败（可能缺少 Microsoft Edge WebView2 运行时）。\n" +
                "请安装 WebView2 Runtime 后重试：https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                ex.Message, "串口终端", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_lifetime.IsClosing) return;
            if (!args.IsSuccess)
            {
                TerminalLoadingText.Text = "终端页面加载失败，请关闭后重试。";
                return;
            }

            _ready = true;
            TerminalWindowPresentation.RevealBrowser(
                () => Web.Visibility = Visibility.Visible,
                () => TerminalLoadingHost.Visibility = Visibility.Collapsed);
            Web.Focus();
            // 回放最近输出：从打开时刻起的数据会实时写入，无需回放
            _viewModel.StateText = "终端就绪";
        });
    }

    private async Task CleanupWebViewAsync()
    {
        try
        {
            await _lifetime.DisposeAfterInitializationAsync(_initializationTask, () =>
            {
                if (Web.CoreWebView2 is { } core)
                {
                    core.WebMessageReceived -= OnWebMessage;
                    core.NavigationCompleted -= OnNavigationCompleted;
                }
                Web.Dispose();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("terminal cleanup: " + ex.Message);
        }
    }

    // ---------- 快捷命令栏（Xshell 式） ----------

    // 工具栏按钮切换命令栏显隐；编辑入口在栏内「编辑」。
    private void OnCommandsClick(object sender, RoutedEventArgs e)
    {
        if (_lifetime.IsClosing) return;
        var show = CommandBarHost.Visibility != Visibility.Visible;
        CommandBarHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AppServices.Settings.General.ShowCommandBar = show;
        AppServices.SaveSettingsSafe();
        if (show) RebuildCommandBar();
        RefocusTerminal();
    }

    private void OnCommandBarEditClick(object sender, RoutedEventArgs e)
    {
        if (_lifetime.IsClosing) return;
        var dialog = new CommandPaletteWindow(_runtime) { Owner = this };
        dialog.ShowDialog();
        RefocusTerminal();
    }

    private void OnCommandBarStopClick(object sender, RoutedEventArgs e) =>
        _commandBarCancellation?.Cancel();

    private void OnPresetsSaved()
    {
        if (_lifetime.IsClosing) return;
        RebuildCommandBar();
    }

    private void RebuildCommandBar()
    {
        if (CommandBarHost.Visibility != Visibility.Visible || _lifetime.IsClosing) return;
        CommandBarButtons.Children.Clear();
        var presets = AppServices.Settings.CommandPresets;
        if (presets.Count == 0)
        {
            SetCommandBarStatus(EmptyCommandBarHint);
            return;
        }
        if (CommandBarStatus.Text == EmptyCommandBarHint) SetCommandBarStatus("");
        foreach (var preset in presets)
        {
            var title = new TextBlock
            {
                Text = CommandPresetText.ButtonTitle(preset),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 160
            };
            var button = new Wpf.Ui.Controls.Button
            {
                Content = title,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = CommandPresetText.Describe(preset)
            };
            button.Click += async (_, _) => await SendPresetFromBarAsync(preset);
            CommandBarButtons.Children.Add(button);
        }
        // 重建可能发生在宏运行中（保存预设触发），恢复禁用状态。
        if (_commandBarCancellation is not null) SetCommandBarButtonStates(false);
    }

    private async Task SendPresetFromBarAsync(CommandPreset preset)
    {
        if (_lifetime.IsClosing || _commandBarCancellation is not null) return;
        try
        {
            // 冻结点击时刻的预设快照，运行期间的编辑/删除不影响本次发送。
            var snapshot = JsonSerializer.Deserialize<CommandPreset>(JsonSerializer.Serialize(preset));
            if (snapshot is null) return;
            try
            {
                foreach (var step in snapshot.Steps) CommandMacroRunner.Encode(step);
            }
            catch (FormatException ex)
            {
                SetCommandBarStatus($"「{CommandPresetText.ButtonTitle(snapshot)}」配置无效：{ex.Message}", error: true);
                return;
            }
            try
            {
                CommandPresetSender.ThrowIfNotConnected(_runtime);
            }
            catch (IOException ex)
            {
                SetCommandBarStatus(ex.Message, error: true);
                return;
            }

            // 点击即发送（无确认弹窗）；多步宏可用「停止」中断后续步骤。
            var cancellation = new CancellationTokenSource();
            _commandBarCancellation = cancellation;
            SetCommandBarButtonStates(false);
            CommandBarStop.Visibility = Visibility.Visible;
            SetCommandBarStatus(snapshot.Steps.Count == 1
                ? $"发送「{CommandPresetText.ButtonTitle(snapshot)}」…"
                : $"执行宏「{CommandPresetText.ButtonTitle(snapshot)}」…");
            // 断开/停止映射后永久中止；单步命令瞬时入队，多步宏可用「停止」中断。
            await CommandPresetSender.SendAsync(_runtime, snapshot, cancellation.Token);
            SetCommandBarStatus($"已发送「{CommandPresetText.ButtonTitle(snapshot)}」");
        }
        catch (OperationCanceledException)
        {
            SetCommandBarStatus("宏已中止。");
        }
        catch (Exception ex)
        {
            SetCommandBarStatus("发送失败：" + ex.Message, error: true);
        }
        finally
        {
            _commandBarCancellation = null;
            SetCommandBarButtonStates(true);
            CommandBarStop.Visibility = Visibility.Collapsed;
            RefocusTerminal();
        }
    }

    private void SetCommandBarButtonStates(bool enabled)
    {
        foreach (var child in CommandBarButtons.Children.OfType<UIElement>())
            child.IsEnabled = enabled;
    }

    private void SetCommandBarStatus(string text, bool error = false)
    {
        CommandBarStatus.Text = text;
        CommandBarStatus.Foreground = error ? CommandBarError : CommandBarMuted;
        CommandBarStatus.ToolTip = text;
    }

    private void RefocusTerminal()
    {
        if (!_ready || _lifetime.IsClosing) return;
        Web.Focus();
        _ = Web.ExecuteScriptAsync("window.focusTerm()");
    }

    // JS 按键 → 串口
    private void OnFindText(object sender, RoutedEventArgs e)
    {
        if (_ready && !_lifetime.IsClosing)
        {
            Web.Focus();
            _ = Web.ExecuteScriptAsync("window.openTerminalFind()");
        }
    }

    private void OnClearScreen(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_ready) _ = Web.ExecuteScriptAsync("window.clearTerm()");
    }

    // ---------- 自绘标题栏 ----------

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { /* 已在拖动中 */ }
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (_lifetime.IsClosing) return;
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "copy":
                    var selection = root.GetProperty("text").GetString();
                    if (!string.IsNullOrEmpty(selection)) Clipboard.SetText(selection);
                    break;
                case "paste":
                    if (!_ready || !Clipboard.ContainsText()) return;
                    var clipboardText = Clipboard.GetText();
                    // 使用 xterm 的粘贴入口，保留换行处理及 bracketed paste 模式。
                    await Web.ExecuteScriptAsync($"window.pasteText({JsonSerializer.Serialize(clipboardText)})");
                    break;
                case "input":
                    var text = root.GetProperty("text").GetString();
                    if (text is null) return;
                    var bytes = Encoding.UTF8.GetBytes(text);
                    _runtime.WriteLocal(bytes);
                    _viewModel.NoteSent(bytes.Length);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("terminal message: " + ex.Message);
            if (!_lifetime.IsClosing) _viewModel.StateText = "终端操作失败：" + ex.Message;
        }
    }

    // 设备输出 → xterm
    private void OnDeviceData(SerialBridge.Core.Bridge.MonitorEntry entry)
    {
        if (entry.Direction != SerialBridge.Core.Models.DataDirection.SerialToNet) return;
        if (_lifetime.IsClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        var b64 = Convert.ToBase64String(entry.Data);
        Dispatcher.BeginInvoke(() =>
        {
            if (!_ready || _lifetime.IsClosing) return;
            _ = Web.ExecuteScriptAsync($"window.writeB64('{b64}')");
            _viewModel.NoteReceived(entry.Data.Length);
        });
    }
}

/// <summary>
/// Orders terminal surface transitions so the dark WPF layer always covers WebView2 creation/destruction.
/// Kept UI-framework agnostic so the ordering can be regression-tested without starting a window.
/// </summary>
internal static class TerminalWindowPresentation
{
    internal static void RevealBrowser(Action showBrowser, Action hidePlaceholder)
    {
        showBrowser();
        hidePlaceholder();
    }

    internal static bool HideForClose(bool isCancelled, Action hideBrowser, Action hideWindow)
    {
        if (isCancelled) return false;
        hideBrowser();
        hideWindow();
        return true;
    }
}

/// <summary>Coordinates cancellation and deferred WebView2 disposal for one terminal window.</summary>
internal sealed class TerminalWindowLifetime
{
    private readonly CancellationTokenSource _cancellation = new();

    internal bool IsClosing { get; private set; }
    internal CancellationToken Token => _cancellation.Token;

    internal void BeginClose()
    {
        if (IsClosing) return;
        IsClosing = true;
        _cancellation.Cancel();
    }

    internal async Task DisposeAfterInitializationAsync(Task? initialization, Action dispose)
    {
        BeginClose();
        if (initialization is not null)
        {
            try
            {
                await initialization;
            }
            catch
            {
                // Cleanup must continue even when initialization ended with an error.
            }
        }

        try
        {
            dispose();
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}
