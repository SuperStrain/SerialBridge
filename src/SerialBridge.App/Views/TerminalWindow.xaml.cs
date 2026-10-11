using System.Collections.Specialized;
using System.Windows.Input;
using SerialBridge.App.ViewModels;
using SerialBridge.Core.Bridge;
using Wpf.Ui.Controls;

namespace SerialBridge.App.Views;

public partial class TerminalWindow : FluentWindow
{
    private static readonly Dictionary<Guid, TerminalWindow> OpenWindows = new();

    private readonly TerminalViewModel _viewModel;

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
        _viewModel = new TerminalViewModel(runtime);
        DataContext = _viewModel;
        Closed += (_, _) =>
        {
            _viewModel.Detach();
            OpenWindows.Remove(runtime.Config.Id);
        };
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_viewModel.Lines is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged += (_, _) =>
            {
                if (OutputList.Items.Count > 0)
                    OutputList.ScrollIntoView(OutputList.Items[^1]);
            };
        }
        InputBox.Focus();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (_viewModel.SendCommand.CanExecute(null))
                    _viewModel.SendCommand.Execute(null);
                break;
            case Key.Up:
                e.Handled = true;
                _viewModel.OnHistoryKey(up: true);
                break;
            case Key.Down:
                e.Handled = true;
                _viewModel.OnHistoryKey(up: false);
                break;
        }
    }
}
