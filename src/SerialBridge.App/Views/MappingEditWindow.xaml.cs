using System.Windows;
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

    /// <summary>要编辑的映射（null 表示新增）。</summary>
    public MappingViewModel? EditTarget { get; init; }

    public MappingEditWindow(Window? owner)
    {
        InitializeComponent();
        Owner = owner;
        _viewModel = new MappingEditViewModel(EditTarget?.Config);
        DataContext = _viewModel;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var config = _viewModel.Build();
        if (config is null) return;

        // 与运行中的其他映射做互斥检查
        if (EditTarget is not null)
        {
            var error = AppServices.Manager.ValidateNew(config, EditTarget.Runtime);
            if (error is not null)
            {
                _viewModel.FailExternal(error);
                return;
            }

            var wasRunning = EditTarget.Runtime.IsRunning;
            _ = ApplyEditAsync(config, wasRunning);
        }
        else
        {
            var rt = AppServices.Manager.CreateMapping(config);
            if (config.AutoStart)
                _ = StartNewAsync(rt);
            AppServices.SaveSettingsSafe();
            _viewModel.MarkConfirmed();
            Close();
        }
    }

    private async Task ApplyEditAsync(MappingConfig config, bool wasRunning)
    {
        var rt = EditTarget!.Runtime;
        var error = await AppServices.Manager.ReplaceMappingAsync(rt, config);
        if (error is not null)
        {
            _viewModel.FailExternal(error);
            return;
        }
        AppServices.SaveSettingsSafe();
        if (wasRunning)
        {
            await AppServices.Manager.StartMappingAsync(rt);
            AppServices.SaveSettingsSafe();
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
