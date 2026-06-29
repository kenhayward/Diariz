using System.Net;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class UrlFetchGuardTests
{
    [Theory]
    [InlineData("http://example.com", true)]
    [InlineData("https://example.com", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("file:///etc/passwd", false)]
    public void IsAllowedScheme_OnlyHttpAndHttps(string url, bool expected) =>
        Assert.Equal(expected, UrlFetchGuard.IsAllowedScheme(new Uri(url)));

    [Theory]
    [InlineData("127.0.0.1")]       // loopback
    [InlineData("10.1.2.3")]        // private 10/8
    [InlineData("172.16.5.4")]      // private 172.16/12
    [InlineData("192.168.0.10")]    // private 192.168/16
    [InlineData("169.254.1.1")]     // link-local
    [InlineData("100.100.0.1")]     // CGNAT 100.64/10
    [InlineData("0.0.0.0")]         // unspecified
    [InlineData("224.0.0.1")]       // multicast
    [InlineData("::1")]             // IPv6 loopback
    [InlineData("fd00::1")]         // IPv6 unique-local
    [InlineData("fe80::1")]         // IPv6 link-local
    public void IsBlocked_RejectsInternalAddresses(string ip) =>
        Assert.True(UrlFetchGuard.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]   // example.com
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")] // public IPv6
    public void IsBlocked_AllowsPublicAddresses(string ip) =>
        Assert.False(UrlFetchGuard.IsBlocked(IPAddress.Parse(ip)));
}
