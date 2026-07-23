using System.Net;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class WebhookUrlValidatorTests
{
    private static WebhookUrlValidator With(params string[] ips) =>
        new((_, _) => Task.FromResult(ips.Select(IPAddress.Parse).ToArray()));

    [Fact]
    public async Task Rejects_non_http_scheme()
    {
        var r = await With("1.2.3.4").ValidateAsync("ftp://example.com/x");
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Rejects_unparseable_url()
    {
        var r = await With("1.2.3.4").ValidateAsync("not a url");
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Rejects_private_and_loopback_targets()
    {
        Assert.False((await With("127.0.0.1").ValidateAsync("https://internal.local/x")).Ok);
        Assert.False((await With("10.0.0.5").ValidateAsync("https://internal.local/x")).Ok);
        Assert.False((await With("169.254.169.254").ValidateAsync("https://metadata/x")).Ok);
    }

    [Fact]
    public async Task Allows_a_public_https_target()
    {
        var r = await With("93.184.216.34").ValidateAsync("https://hooks.example.com/abc");
        Assert.True(r.Ok);
        Assert.Null(r.Reason);
    }

    [Fact]
    public async Task Rejects_when_any_resolved_ip_is_blocked()
    {
        // A public A-record plus a private one (DNS-rebinding style) must be rejected.
        var r = await With("93.184.216.34", "10.1.2.3").ValidateAsync("https://sneaky.example.com/x");
        Assert.False(r.Ok);
    }
}
