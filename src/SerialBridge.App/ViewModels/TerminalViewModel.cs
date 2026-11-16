using CommunityToolkit.Mvvm.ComponentModel;
using SerialBridge.Core.Bridge;

namespace SerialBridge.App.ViewModels;

/// <summary>Transport-neutral terminal status for local COM and remote TCP sessions.</summary>
public partial class TerminalViewModel : ObservableObject
{
    private readonly ITerminalSession _session;

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
        OnStatusChanged();
    }

    private void OnStatusChanged()
    {
        TxBytes = _session.SentBytes;
        RxBytes = _session.ReceivedBytes;
        TxText = MappingViewModel.FormatBytes(TxBytes);
        RxText = MappingViewModel.FormatBytes(RxBytes);
        StateText = _session.IsConnected ? "已连接 — 逐键透传中" : "连接已断开 — 请重新连接";
    }

    public void NoteSent(int bytesCount) => OnStatusChanged();
    public void NoteReceived(int bytesCount) => OnStatusChanged();
    public void Detach() => _session.StateChanged -= OnStatusChanged;
}
