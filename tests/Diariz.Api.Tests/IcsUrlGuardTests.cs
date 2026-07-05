using System.Net;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The pure, DNS-free half of the SSRF guard for external .ics feed URLs. <see cref="IcsUrlGuard"/>
/// rejects anything that isn't a plain https URL to a public host by inspection alone; the resolved-IP
/// re-check at fetch time (which needs DNS) is exercised via <see cref="IcsUrlGuard.IsBlockedAddress"/>.</summary>
public class IcsUrlGuardTests
{
    [Theory]
    [InlineData("https://example.com/team.ics")]
    [InlineData("https://calendar.google.com/calendar/ical/abc/basic.ics")]
    [InlineData("https://host.example.co.uk:8443/feed.ics")]
    public void ValidateSyntax_AllowsPublicHttpsUrls(string url)
    {
        var (ok, error) = IcsUrlGuard.ValidateSyntax(url);
        Assert.True(ok, error);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("http://example.com/team.ics")]       // not https
    [InlineData("ftp://example.com/team.ics")]         // wrong scheme
    [InlineData("webcal://example.com/team.ics")]      // webcal not accepted (client must be given https)
    [InlineData("file:///etc/passwd")]                 // no host / local file
    [InlineData("not a url")]                            // unparseable
    [InlineData("")]                                    // empty
    [InlineData("https://")]                            // no host
    public void ValidateSyntax_RejectsNonHttpsOrHostless(string url)
    {
        var (ok, error) = IcsUrlGuard.ValidateSyntax(url);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("https://localhost/feed.ics")]
    [InlineData("https://LOCALHOST/feed.ics")]
    [InlineData("https://foo.local/feed.ics")]
    [InlineData("https://127.0.0.1/feed.ics")]
    [InlineData("https://10.0.0.5/feed.ics")]
    [InlineData("https://192.168.1.10/feed.ics")]
    [InlineData("https://172.16.4.4/feed.ics")]
    [InlineData("https://169.254.169.254/latest/meta-data")] // cloud metadata endpoint
    [InlineData("https://[::1]/feed.ics")]
    [InlineData("https://[fd00::1]/feed.ics")]
    public void ValidateSyntax_RejectsPrivateAndLoopbackLiterals(string url)
    {
        var (ok, error) = IcsUrlGuard.ValidateSyntax(url);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456::1")]
    public void IsBlockedAddress_BlocksPrivateAndSpecial(string ip)
    {
        Assert.True(IcsUrlGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("142.250.72.14")]
    [InlineData("2606:4700:4700::1111")]
    public void IsBlockedAddress_AllowsPublic(string ip)
    {
        Assert.False(IcsUrlGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }
}
