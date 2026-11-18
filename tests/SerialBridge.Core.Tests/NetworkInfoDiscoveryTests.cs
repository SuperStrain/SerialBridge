using System.Net;
using SerialBridge.Core.Net;
using Xunit;

namespace SerialBridge.Core.Tests;

public sealed class NetworkInfoDiscoveryTests
{
    [Theory]
    [InlineData("172.20.8.211", "172.20.8.0/24")]
    [InlineData("192.168.31.101", "192.168.31.0/24")]
    [InlineData("10.20.3.5", "10.20.3.0/24")]
    public void DiscoveryDefaultSubnetFollowsActualLocalIp(string host, string expected)
    {
        Assert.Equal(expected,
            NetworkInfo.DiscoverySubnetFromAddress(IPAddress.Parse(host)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void DiscoveryDefaultSubnetRejectsLoopbackAndNonIpv4(string host)
    {
        Assert.Throws<ArgumentException>(
            () => NetworkInfo.DiscoverySubnetFromAddress(IPAddress.Parse(host)));
    }
}
