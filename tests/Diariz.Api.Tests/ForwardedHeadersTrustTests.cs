using System.Net;
using Diariz.Api.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>X-Forwarded-Proto/For are believed only from the reverse proxy's side of the network. A caller reaching
/// the API from anywhere else keeps its real scheme and address.</summary>
public class ForwardedHeadersTrustTests
{
    private static async Task<HttpContext> Send(string remoteIp)
    {
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersTrust.Configure(options);

        HttpContext? seen = null;
        var middleware = new ForwardedHeadersMiddleware(
            ctx => { seen = ctx; return Task.CompletedTask; }, NullLoggerFactory.Instance, Options.Create(options));

        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        http.Request.Scheme = "http";
        http.Request.Headers["X-Forwarded-Proto"] = "https";
        http.Request.Headers["X-Forwarded-For"] = "203.0.113.50";
        await middleware.Invoke(http);
        return seen!;
    }

    [Theory]
    [InlineData("172.18.0.5")]        // a compose bridge network
    [InlineData("10.1.2.3")]
    [InlineData("192.168.1.20")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:172.18.0.5")] // Kestrel reports IPv4 peers as mapped IPv6 on a dual-stack socket
    [InlineData("fd12:3456::7")]      // IPv6 unique-local
    public async Task AProxyOnAPrivateNetwork_IsBelieved(string proxyIp)
    {
        var ctx = await Send(proxyIp);
        Assert.Equal("https", ctx.Request.Scheme);
        Assert.Equal(IPAddress.Parse("203.0.113.50"), ctx.Connection.RemoteIpAddress);
    }

    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:db8::1")]
    [InlineData("172.32.0.1")]        // just outside 172.16.0.0/12
    public async Task ACallerOnAPublicAddress_IsNotBelieved(string callerIp)
    {
        var ctx = await Send(callerIp);
        Assert.Equal("http", ctx.Request.Scheme);
        Assert.Equal(IPAddress.Parse(callerIp), ctx.Connection.RemoteIpAddress);
    }
}
