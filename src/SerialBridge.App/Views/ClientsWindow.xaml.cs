using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using SerialBridge.App.ViewModels;
using SerialBridge.Core.Bridge;
using Wpf.Ui.Controls;

namespace SerialBridge.App.Views;

public partial class ClientsWindow : FluentWindow
{
    private static readonly Dictionary<Guid, ClientsWindow> OpenWindows = new();

    private readonly ClientsViewModel _viewModel;

    /// <summary>打开（或激活）指定映射的客户端详情窗口。</summary>
    public static void OpenFor(MappingRuntime runtime)
    {
        if (OpenWindows.TryGetValue(runtime.Config.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new ClientsWindow(runtime);
        OpenWindows[runtime.Config.Id] = window;
        window.Show();
    }

    public ClientsWindow(MappingRuntime runtime)
    {
        InitializeComponent();
        _viewModel = new ClientsViewModel(runtime);
        DataContext = _viewModel;
        Closed += (_, _) => { _viewModel.Stop(); OpenWindows.Remove(runtime.Config.Id); };
    }
}

/// <summary>一行客户端快照。</summary>
public sealed class ClientRow
{
    public string Remote { get; init; } = "";
    public string ConnectedAtText { get; init; } = "";
    public string DurationText { get; init; } = "";
    public string SentText { get; init; } = "";
    public string RecvText { get; init; } = "";
}

/// <summary>客户端详情 ViewModel：每秒刷新快照（连接列表 + 收发计数）。</summary>
public sealed class ClientsViewModel : INotifyPropertyChanged
{
    private readonly MappingRuntime _runtime;
    private readonly DispatcherTimer _timer;

    public string Title { get; }
    public ObservableCollection<ClientRow> Clients { get; } = new();

    private int _clientCount;
    private long _totalAccepted;

    public int ClientCount { get => _clientCount; private set { _clientCount = value; PropertyChanged?.Invoke(this, new(nameof(ClientCount))); } }
    public long TotalAccepted { get => _totalAccepted; private set { _totalAccepted = value; PropertyChanged?.Invoke(this, new(nameof(TotalAccepted))); } }
    public bool HasClients => ClientCount > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ClientsViewModel(MappingRuntime runtime)
    {
        _runtime = runtime;
        Title = $"客户端详情 — {runtime.Config.Name}";
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var conns = _runtime.Tcp.Clients
            .OrderBy(c => c.ConnectedAtUtc)
            .ToList();
        ClientCount = conns.Count;
        TotalAccepted = _runtime.TotalAcceptedClients;

        Clients.Clear();
        foreach (var c in conns)
        {
            var connectedFor = DateTime.UtcNow - c.ConnectedAtUtc;
            Clients.Add(new ClientRow
            {
                Remote = c.RemoteEndPoint,
                ConnectedAtText = c.ConnectedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
                DurationText = connectedFor.TotalHours >= 1
                    ? $"{(int)connectedFor.TotalHours}时{connectedFor.Minutes}分"
                    : $"{connectedFor.Minutes}分{connectedFor.Seconds}秒",
                SentText = MappingViewModel.FormatBytes(c.SentBytes),
                RecvText = MappingViewModel.FormatBytes(c.RecvBytes),
            });
        }
        PropertyChanged?.Invoke(this, new(nameof(HasClients)));
    }

    public void Stop() => _timer.Stop();
}
