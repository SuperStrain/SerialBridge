namespace SerialBridge.Core.Models;

/// <summary>Saved outgoing TCP endpoint. No physical COM port is opened locally.</summary>
public sealed class RemoteConnectionConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "远程设备";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9001;
    public bool AutoReconnect { get; set; }
    public bool AutoConnectOnStartup { get; set; }
}
