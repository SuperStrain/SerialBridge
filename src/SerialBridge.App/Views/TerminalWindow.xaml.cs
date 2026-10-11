using System.IO;
using System.Text;
using System.Windows;
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

        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closed += (_, _) =>
        {
            _viewModel.Detach();
            _runtime.DataTransferred -= OnDeviceData;
            OpenWindows.Remove(runtime.Config.Id);
        };
        _runtime.DataTransferred += OnDeviceData;
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SerialBridge", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Web.EnsureCoreWebView2Async(env);

            // 本地资源映射（免 HTTP 服务、离线可用）
            var terminalDir = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "terminal.serialbridge.local", terminalDir,
                CoreWebView2HostResourceAccessKind.Allow);

            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _ready = true;
                Dispatcher.BeginInvoke(() =>
                {
                    Web.Focus();
                    // 回放最近输出：从打开时刻起的数据会实时写入，无需回放
                    _viewModel.StateText = "终端就绪";
                });
            };
            Web.Source = new Uri("https://terminal.serialbridge.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "终端组件初始化失败（可能缺少 Microsoft Edge WebView2 运行时）。\n" +
                "请安装 WebView2 Runtime 后重试：https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                ex.Message, "串口终端", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // JS 按键 → 串口
    private void OnClearScreen(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_ready) _ = Web.ExecuteScriptAsync("window.clearTerm()");
    }

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
        var b64 = Convert.ToBase64String(entry.Data);
        Dispatcher.BeginInvoke(() =>
        {
            if (!_ready) return;
            _ = Web.ExecuteScriptAsync($"window.writeB64('{b64}')");
            _viewModel.NoteReceived(entry.Data.Length);
        });
    }
}
