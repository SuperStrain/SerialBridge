using System.Windows;
using Wpf.Ui.Controls;

namespace SerialBridge.App.Views;

public partial class LinuxAccessWindow : FluentWindow
{
    private static LinuxAccessWindow? _instance;

    public LinuxAccessWindow(Window? owner)
    {
        InitializeComponent();
        Owner = owner;
    }

    /// <summary>单例打开（避免堆叠多个帮助窗口）。</summary>
    public static void Open(Window? owner)
    {
        if (_instance is { IsLoaded: true })
        {
            _instance.Activate();
            return;
        }
        _instance = new LinuxAccessWindow(owner);
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
    }
}
