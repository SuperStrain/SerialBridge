using SerialBridge.Core.Tcp;

namespace SerialBridge.Core.Bridge;

/// <summary>Minimal transport-independent view used only by terminal rendering and commands.</summary>
public interface ITerminalSession
{
    Guid Id { get; }
    string Title { get; }
    string ConnectionDescription { get; }
    bool IsConnected { get; }
    long SentBytes { get; }
    long ReceivedBytes { get; }
    bool Send(byte[] data);
    event Action<byte[]>? Received;
    event Action? StateChanged;
}

/// <summary>Adapter for existing direct COM terminal; no local loopback TCP involved.</summary>
public sealed class LocalTerminalSession : ITerminalSession, IDisposable
{
    private readonly MappingRuntime _runtime;
    public Guid Id => _runtime.Config.Id;
    public string Title => "本机串口 — " + _runtime.Config.Name;
    public string ConnectionDescription => _runtime.Config.SerialSummary;
    public bool IsConnected => _runtime.State == Models.MappingState.Running &&
        _runtime.Serial.State == Models.SerialState.Open;
    public long SentBytes => _runtime.SerialTxBytes;
    public long ReceivedBytes => _runtime.SerialRxBytes;
    public event Action<byte[]>? Received;
    public event Action? StateChanged;
    public LocalTerminalSession(MappingRuntime runtime)
    {
        _runtime = runtime;
        runtime.DataTransferred += OnData;
        runtime.StatusChanged += OnState;
    }
    private void OnData(MonitorEntry entry)
    {
        if (entry.Direction == Models.DataDirection.SerialToNet)
            Received?.Invoke(entry.Data);
    }
    private void OnState(MappingRuntime _) => StateChanged?.Invoke();
    public bool Send(byte[] data) => _runtime.WriteLocal(data);
    public void Dispose()
    {
        _runtime.DataTransferred -= OnData;
        _runtime.StatusChanged -= OnState;
    }
}

/// <summary>Adapter for outgoing TCP connection; terminal visibility does not own the socket.</summary>
public sealed class RemoteTerminalSession : ITerminalSession, IDisposable
{
    private readonly RemoteTcpSession _remote;
    public Guid Id => _remote.Config.Id;
    public string Title => "远程 TCP — " + _remote.Config.Name;
    public string ConnectionDescription => _remote.Config.Host + ":" + _remote.Config.Port;
    public bool IsConnected => _remote.IsConnected;
    public long SentBytes => _remote.SentBytes;
    public long ReceivedBytes => _remote.ReceivedBytes;
    public event Action<byte[]>? Received;
    public event Action? StateChanged;
    public RemoteTerminalSession(RemoteTcpSession remote)
    {
        _remote = remote;
        remote.DataReceived += OnData;
        remote.StatusChanged += OnState;
    }
    private void OnData(byte[] bytes) => Received?.Invoke(bytes);
    private void OnState() => StateChanged?.Invoke();
    public bool Send(byte[] data) => _remote.Send(data);
    public void Dispose()
    {
        _remote.DataReceived -= OnData;
        _remote.StatusChanged -= OnState;
    }
}
