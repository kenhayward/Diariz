using System.Net;
using System.Security.Cryptography;
using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class WebhookDeliveryIntegrationTests(ContainersFixture fx)
{
    private sealed class PlainProtector : IWebhookSecretProtector
    { public string? Protect(string? p) => p; public string? Unprotect(string? c) => c; }

    [Fact]
    public async Task Delivers_signed_payload_to_a_real_endpoint()
    {
        // A tiny local sink on a loopback port. The SSRF guard is on the CONTROLLER create path, not the
        // processor, so delivering to 127.0.0.1 here is fine and intentional.
        using var listener = new HttpListener();
        var port = GetFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? receivedBody = null, receivedSig = null, receivedTs = null;
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            receivedBody = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            receivedSig = ctx.Request.Headers["webhook-signature"];
            receivedTs = ctx.Request.Headers["webhook-timestamp"];
            ctx.Response.StatusCode = 200; ctx.Response.Close();
        });

        await using var db = fx.CreateDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId:N}@e.com", UserName = $"{userId:N}@e.com" });
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "sink", Url = $"http://127.0.0.1:{port}/hook",
            SecretEncrypted = "topsecret", EventTypes = "recording.transcribed", IsActive = true,
        };
        db.Webhooks.Add(sub);
        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = sub.Id, EventId = "evt_int", EventType = "recording.transcribed",
            PayloadJson = "{\"id\":\"evt_int\"}", NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var processor = new WebhookDeliveryProcessor(new PlainProtector(),
            Options.Create(new WebhookOptions()), NullLogger<WebhookDeliveryProcessor>.Instance);
        using var http = new HttpClient();
        await processor.ProcessDueAsync(db, http, DateTimeOffset.UtcNow, default);
        await serve;
        listener.Stop();

        Assert.Equal("{\"id\":\"evt_int\"}", receivedBody);
        var expected = "v1," + Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes("topsecret"))
            .ComputeHash(Encoding.UTF8.GetBytes($"evt_int.{receivedTs}.{{\"id\":\"evt_int\"}}")));
        Assert.Equal(expected, receivedSig);
        Assert.Equal(WebhookDeliveryStatus.Delivered,
            (await db.WebhookDeliveries.SingleAsync(d => d.EventId == "evt_int")).Status);
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start(); var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
}
