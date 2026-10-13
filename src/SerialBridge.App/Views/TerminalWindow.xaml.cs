using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using SerialBridge.App.ViewModels;
using SerialBridge.Core.Bridge;

namespace SerialBridge.App.Views;

/// <summary>
/// 串口终端（Xshell 式哑终端）：WebView2 + xterm.js 渲染设备输出，
/// 用户按键（onData）逐字节透传 MappingRuntime.WriteLocal 到设备。
/// 本地不回显——与真实串口一致，所见即设备回显。
/// </summary>
public partial class TerminalWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly Dictionary<Guid, TerminalWindow> OpenWindows = new();

    private readonly TerminalViewModel _viewModel;
    private readonly MappingRuntime _runtime;
    private readonly TerminalWindowLifetime _lifetime = new();
    private Task? _initializationTask;
    private bool _ready;

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
            OpenWindows.Remove(runtime.Config.Id);
            _ = CleanupWebViewAsync();
        };
        _runtime.DataTransferred += OnDeviceData;
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

    // JS 按键 → 串口
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

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            // 简单解析：{"type":"input","text":"..."} / {"type":"ready"}
            if (json.Contains("\"ready\"", StringComparison.Ordinal)) return;
            if (!json.Contains("\"input\"", StringComparison.Ordinal)) return;
            var text = ExtractJsonStringField(json, "text");
            if (text is null) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            _runtime.WriteLocal(bytes);
            _viewModel.NoteSent(bytes.Length);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("terminal message: " + ex.Message);
        }
    }

    private static string? ExtractJsonStringField(string json, string field)
    {
        // 轻量提取（避免引入 JSON 库）：匹配 "field":"..."，处理常见转义
        var key = $"\"{field}\":";
        var idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = json.IndexOf('"', idx + key.Length);
        if (start < 0) return null;
        var sb = new System.Text.StringBuilder();
        for (var i = start + 1; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '"' && json[i - 1] != '\\') break;
            if (c == '\\' && i + 1 < json.Length)
            {
                var n = json[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'u':
                        if (i + 4 < json.Length &&
                            ushort.TryParse(json.AsSpan(i + 1, 4),
                                System.Globalization.NumberStyles.HexNumber, null, out var code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(n); break;
                }
            }
            else sb.Append(c);
        }
        return sb.ToString();
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
