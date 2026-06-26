using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class RedisJobQueueIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task EnqueueAsync_AddsJsonJobToTheStream()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var opts = Options.Create(new JobQueueOptions { StreamKey = $"jobs-{Guid.NewGuid()}" });
        var queue = new RedisJobQueue(mux, opts);

        var job = new TranscriptionJob(Guid.NewGuid(), Guid.NewGuid(), "user/blob.webm", "whisperx-large-v3");
        await queue.EnqueueAsync(job);

        // The Python worker reads this exact shape: one stream entry with a PascalCase-JSON "job" field.
        var entries = await mux.GetDatabase().StreamRangeAsync(opts.Value.StreamKey, "-", "+");
        var entry = Assert.Single(entries);
        var jobField = entry.Values.Single(v => v.Name == "job");
        var json = jobField.Value.ToString();

        var roundTripped = JsonSerializer.Deserialize<TranscriptionJob>(json);
        Assert.Equal(job.TranscriptionId, roundTripped!.TranscriptionId);
        Assert.Equal(job.RecordingId, roundTripped.RecordingId);
        Assert.Equal("user/blob.webm", roundTripped.BlobKey);
        Assert.Equal("whisperx-large-v3", roundTripped.Model);

        // Guard the wire contract: keys must stay PascalCase or the worker breaks.
        Assert.Contains("\"BlobKey\"", json);
    }
}
