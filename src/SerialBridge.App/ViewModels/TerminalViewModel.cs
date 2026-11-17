using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SerialBridge.Core.Bridge;

namespace SerialBridge.App.ViewModels;

/// <summary>Transport-neutral terminal status for local COM and remote TCP sessions.</summary>
public partial class TerminalViewModel : ObservableObject
{
    private readonly ITerminalSession _session;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    [ObservableProperty] private string _title = "串口终端";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private long _txBytes;
    [ObservableProperty] private long _rxBytes;
    [ObservableProperty] private string _rxText = "0 B";
    [ObservableProperty] private string _txText = "0 B";

    public TerminalViewModel(ITerminalSession session)
    {
        _session = session;
        Title = session.Title + " · " + session.ConnectionDescription;
        session.StateChanged += OnStatusChanged;
        RefreshStatus();
    }

    private void OnStatusChanged()
    {
        if (_ui is { } context && SynchronizationContext.Current != context)
            context.Post(_ => RefreshStatus(), null);
        else
            RefreshStatus();
    }

    // Session configuration can change while its terminal window remains open.
    // Refresh the identity together with connection state before more input is sent.
    public void RefreshStatus()
    {
        Title = _session.Title + " · " + _session.ConnectionDescription;
        TxBytes = _session.SentBytes;
        RxBytes = _session.ReceivedBytes;
        TxText = MappingViewModel.FormatBytes(TxBytes);
        RxText = MappingViewModel.FormatBytes(RxBytes);
        StateText = _session.IsConnected ? "已连接" : "连接已断开";
    }

    public void NoteSent(int bytesCount) => OnStatusChanged();
    public void NoteReceived(int bytesCount) => OnStatusChanged();
    public void Detach() => _session.StateChanged -= OnStatusChanged;
}
