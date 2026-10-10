using System.Windows;
using SerialBridge.App.ViewModels;
using SerialBridge.Core.Bridge;
using Wpf.Ui.Controls;

namespace SerialBridge.App.Views;

public partial class MonitorWindow : FluentWindow
{
    private static readonly Dictionary<Guid, MonitorWindow> OpenWindows = new();

    private readonly MonitorViewModel _viewModel;

    /// <summary>打开（或激活）指定映射的监控窗口。</summary>
    public static void OpenFor(MappingRuntime runtime)
    {
        if (OpenWindows.TryGetValue(runtime.Config.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new MonitorWindow(runtime);
        OpenWindows[runtime.Config.Id] = window;
        window.Show();
    }

    public MonitorWindow(MappingRuntime runtime)
    {
        InitializeComponent();
        _viewModel = new MonitorViewModel(runtime);
        DataContext = _viewModel;
        Closed += (_, _) =>
        {
            _viewModel.Detach();
            OpenWindows.Remove(runtime.Config.Id);
        };
    }

    /// <summary>数据到达时保持自动滚动（由 VM 批量刷新后触发滚动到底部）。</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_viewModel.AutoScroll && _viewModel.Lines is System.Collections.Specialized.INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged += (_, _) =>
            {
                if (_viewModel.AutoScroll && LinesList.Items.Count > 0)
                    LinesList.ScrollIntoView(LinesList.Items[^1]);
            };
        }
    }
}
