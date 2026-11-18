using System.Windows;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using SerialBridge.App.Services;
using SerialBridge.App.ViewModels;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Models;
using Wpf.Ui.Controls;

namespace SerialBridge.App.Views;

public partial class MappingEditWindow : FluentWindow
{
    private readonly MappingEditViewModel _viewModel;

    /// <summary>要编辑的映射（null 表示新增）。必须通过构造参数传入：</summary>
    /// <remarks>此前经对象初始化器赋值，而构造函数先执行，导致 source 恒为 null、
    /// 编辑时加载的全是默认值（本 bug 同时是串口被意外改成默认值的根源）。</remarks>
    public MappingEditWindow(Window? owner, MappingViewModel? editTarget)
    {
        InitializeComponent();
        Owner = owner;
        EditTarget = editTarget;
        _viewModel = new MappingEditViewModel(editTarget?.Config);
        DataContext = _viewModel;
    }

    private MappingViewModel? EditTarget { get; }

    private void ShowSaveError(string message)
    {
        _viewModel.FailExternal(message);
        System.Windows.MessageBox.Show(this, message, "无法保存映射",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private string? CheckExternalPortConflicts(MappingConfig config)
    {
        // Never probe a COM port already owned by the mapping being edited:
        // opening a physical port can disturb connected hardware.
        var current = EditTarget?.Runtime;
        var ownsSerial = current?.IsRunning == true &&
            string.Equals(current.Config.PortName, config.PortName, StringComparison.OrdinalIgnoreCase);
        if (!ownsSerial)
        {
            try
            {
                using var serial = new SerialPort(config.PortName, config.BaudRate,
                    config.Parity, config.DataBits, config.StopBits)
                {
                    DtrEnable = false, RtsEnable = false,
                    Handshake = config.FlowControl,
                };
                serial.Open();
            }
            catch (UnauthorizedAccessException)
            {
                return $"COM 串口 {config.PortName} 已被占用，请关闭占用该串口的程序后重试。";
            }
            catch (IOException ex)
            {
                return $"COM 串口 {config.PortName} 无法打开：{ex.Message}";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return $"COM 串口 {config.PortName} 不可用：{ex.Message}";
            }
        }

        // A live self-mapping already owns its listen endpoint; only probe
        // other endpoints. No packets are sent, just a temporary bind check.
        var ownsEndpoint = current?.IsRunning == true &&
            current.Config.ListenPort == config.ListenPort &&
            (current.Config.ListenIp == config.ListenIp ||
             current.Config.ListenIp == "0.0.0.0");
        if (!ownsEndpoint)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Parse(config.ListenIp), config.ListenPort);
                listener.Start();
                listener.Stop();
            }
            catch (SocketException ex) when (ex.SocketErrorCode is
                SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                return $"TCP 端口 {config.ListenPort} 已被占用，请更换端口或关闭占用程序。";
            }
            catch (Exception ex) when (ex is SocketException or FormatException)
            {
                return $"TCP 端口 {config.ListenPort} 无法监听：{ex.Message}";
            }
        }
        return null;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var config = _viewModel.Build();
        if (config is null)
        {
            ShowSaveError(_viewModel.ErrorMessage);
            return;
        }

        var error = AppServices.Manager.ValidateNew(config, EditTarget?.Runtime)
                    ?? CheckExternalPortConflicts(config);
        if (error is not null)
        {
            ShowSaveError(error);
            return;
        }

        try
        {
            if (EditTarget is not null)
            {
                await ApplyEditAsync(config, EditTarget.Runtime.IsRunning);
            }
            else
            {
                var rt = AppServices.Manager.CreateMapping(config);
                if (config.AutoStart)
                {
                    var startError = await AppServices.Manager.StartMappingAsync(rt);
                    if (startError is not null)
                        ShowSaveError($"映射已保存，但自动启动失败：{startError}");
                }
                AppServices.SaveSettingsSafe();
                _viewModel.MarkConfirmed();
                Close();
            }
        }
        catch (Exception ex)
        {
            ShowSaveError($"保存映射失败：{ex.Message}");
        }
    }

    private async Task ApplyEditAsync(MappingConfig config, bool wasRunning)
    {
        var rt = EditTarget!.Runtime;
        var error = await AppServices.Manager.ReplaceMappingAsync(rt, config);
        if (error is not null)
        {
            ShowSaveError(error);
            return;
        }
        // ReplaceMappingAsync updates the existing config object in place.
        // Notify bindings immediately, including when the mapping stays stopped.
        EditTarget.Update();
        AppServices.SaveSettingsSafe();
        if (wasRunning)
        {
            var startError = await AppServices.Manager.StartMappingAsync(rt);
            EditTarget.Update();
            AppServices.SaveSettingsSafe();
            if (startError is not null) ShowSaveError($"映射已保存，但启动失败：{startError}");
        }
        _viewModel.MarkConfirmed();
        Dispatcher.Invoke(Close);
    }

    private async Task StartNewAsync(MappingRuntime rt)
    {
        var error = await AppServices.Manager.StartMappingAsync(rt);
        if (error is not null)
            LogX.Warn($"映射「{rt.Config.Name}」自动启动失败：{error}");
        AppServices.SaveSettingsSafe();
    }
}
