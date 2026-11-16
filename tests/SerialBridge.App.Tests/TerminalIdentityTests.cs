using SerialBridge.App.ViewModels;
using SerialBridge.Core.Bridge;
using Xunit;

namespace SerialBridge.App.Tests;

public sealed class TerminalIdentityTests
{
    [Fact]
    public void RemoteTargetChange_RefreshesTitleOnReconnectWithoutRecreatingTerminal()
    {
        var session = new MutableTerminalSession
        {
            Title = "远程 TCP — 旧设备",
            ConnectionDescription = "192.168.1.10:9001"
        };
        var vm = new TerminalViewModel(session);
        try
        {
            Assert.Contains("旧设备", vm.Title);
            Assert.Contains("192.168.1.10:9001", vm.Title);

            session.Title = "远程 TCP — 新设备";
            session.ConnectionDescription = "192.168.1.20:9002";
            session.IsConnected = true;
            session.NotifyStateChanged();

            Assert.Contains("新设备", vm.Title);
            Assert.Contains("192.168.1.20:9002", vm.Title);
            Assert.DoesNotContain("旧设备", vm.Title);
            Assert.DoesNotContain("192.168.1.10", vm.Title);
            Assert.Equal("已连接 — 逐键透传中", vm.StateText);
        }
        finally { vm.Detach(); }
    }

    [Fact]
    public void ReopeningExistingTerminalCanRefreshEditedIdentityEvenIfDisconnected()
    {
        var session = new MutableTerminalSession
        {
            Title = "远程 TCP — 设备A",
            ConnectionDescription = "10.0.0.1:9001"
        };
        var vm = new TerminalViewModel(session);
        try
        {
            session.Title = "远程 TCP — 设备B";
            session.ConnectionDescription = "10.0.0.2:9002";
            vm.RefreshStatus();

            Assert.Equal("远程 TCP — 设备B · 10.0.0.2:9002", vm.Title);
        }
        finally { vm.Detach(); }
    }

    private sealed class MutableTerminalSession : ITerminalSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Title { get; set; } = "";
        public string ConnectionDescription { get; set; } = "";
        public bool IsConnected { get; set; }
        public long SentBytes => 0;
        public long ReceivedBytes => 0;
        public event Action<byte[]>? Received;
        public event Action? StateChanged;
        public bool Send(byte[] bytes) => IsConnected;
        public void NotifyStateChanged() => StateChanged?.Invoke();
    }
}
