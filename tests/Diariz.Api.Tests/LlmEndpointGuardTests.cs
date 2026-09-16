using System.Net;
using System.Net.Sockets;
using System.Text;
using Diariz.Api.Services.Llm;

namespace Diariz.Api.Tests;

/// <summary>Where the server's own LLM calls may connect. LLM endpoints legitimately live on the LAN (self-hosted
/// model servers), so private ranges stay reachable; link-local and cloud-metadata addresses never are, and neither
/// is the API's own loopback outside Development.</summary>
public class LlmEndpointGuardTests
{
    [Theory]
    [InlineData("169.254.169.254")]   // cloud instance metadata (AWS/GCP/Azure IMDS)
    [InlineData("169.254.0.1")]       // any IPv4 link-local
    [InlineData("fe80::1")]           // IPv6 link-local
    [InlineData("fd00:ec2::254")]     // AWS IMDS over IPv6
    [InlineData("100.100.100.200")]   // Alibaba Cloud metadata
    [InlineData("168.63.129.16")]     // Azure wire server
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("224.0.0.1")]         // multicast
    [InlineData("::ffff:169.254.169.254")]
    public void MetadataLinkLocalAndUnusableAddresses_AreAlwaysRefused(string ip)
    {
        Assert.True(LlmEndpointGuard.IsBlocked(IPAddress.Parse(ip), allowLoopback: false));
        Assert.True(LlmEndpointGuard.IsBlocked(IPAddress.Parse(ip), allowLoopback: true));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    public void Loopback_IsRefused_UnlessAllowed(string ip)
    {
        Assert.True(LlmEndpointGuard.IsBlocked(IPAddress.Parse(ip), allowLoopback: false));
        Assert.False(LlmEndpointGuard.IsBlocked(IPAddress.Parse(ip), allowLoopback: true));
    }

    [Theory]
    [InlineData("192.168.1.50")]      // a model server on the LAN
    [InlineData("10.0.0.7")]
    [InlineData("172.18.0.9")]        // a container on the compose network
    [InlineData("fd12:3456::9")]
    [InlineData("104.18.6.192")]      // a public API
    [InlineData("2606:4700::6812:6c0")]
    public void PrivateAndPublicAddresses_AreAllowed(string ip) =>
        Assert.False(LlmEndpointGuard.IsBlocked(IPAddress.Parse(ip), allowLoopback: false));

    private static HttpClient Client(bool allowLoopback) =>
        // UseProxy off so these exercise the connect step itself; the proxied path is covered by the handler test.
        new(new SocketsHttpHandler { ConnectCallback = LlmEndpointGuard.ConnectCallback(allowLoopback), UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

    [Fact]
    public async Task AMetadataAddress_IsRefusedBeforeAnyConnectionIsAttempted()
    {
        using var client = Client(allowLoopback: true);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://169.254.169.254/latest/meta-data/"));
        Assert.Contains("not allowed", e.ToString());
    }

    [Fact]
    public async Task Loopback_IsRefused_WhenNotAllowed_AndReachable_WhenAllowed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            var buffer = new byte[4096];
            await socket.ReceiveAsync(buffer);
            await socket.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        });

        using (var refused = Client(allowLoopback: false))
            await Assert.ThrowsAsync<HttpRequestException>(() => refused.GetAsync($"http://127.0.0.1:{port}/"));

        using var allowed = Client(allowLoopback: true);
        var body = await allowed.GetStringAsync($"http://127.0.0.1:{port}/");
        Assert.Equal("ok", body);
        await server;
    }

    [Fact]
    public async Task AHostnameIsCheckedByWhatItResolvesTo()
    {
        // "localhost" is a name, not an address: the guard must look at the resolved addresses, or a DNS name
        // pointing at an internal address would walk straight past an address-only check.
        using var client = Client(allowLoopback: false);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://localhost:9/"));
    }

    [Fact]
    public async Task TheRequestHandler_RefusesABlockedHost_EvenWhenTheConnectionWouldGoThroughAProxy()
    {
        // Through a proxy the socket connects to the proxy, so the connect-step check never sees the real
        // destination. The request handler checks the request's own host before anything is sent.
        var inner = new RecordingHandler();
        using var client = new HttpClient(new LlmEndpointGuardHandler(allowLoopback: false) { InnerHandler = inner });

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://169.254.169.254/latest/meta-data/"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://localhost:1234/v1/models"));
        Assert.Equal(0, inner.Calls);

        await client.GetAsync("http://192.168.1.50:1234/v1/models");
        Assert.Equal(1, inner.Calls);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
