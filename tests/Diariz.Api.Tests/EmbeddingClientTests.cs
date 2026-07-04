using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

public class EmbeddingClientTests
{
    // OpenAI /embeddings response: { data: [ { index, embedding: [...] }, ... ] }
    private static string EmbeddingsResponse(params float[][] vectors)
    {
        var obj = new
        {
            data = vectors.Select((v, i) => new { index = i, embedding = v }).ToArray(),
        };
        return JsonSerializer.Serialize(obj);
    }

    private static EmbeddingRequestConfig Config(int batchSize = 32) =>
        new("http://llm.test/v1", "sk-emb", "nomic-embed-text", 3, 60, batchSize);

    [Fact]
    public async Task EmbedAsync_PostsToEmbeddings_WithBearerAndModel_AndParsesVectors()
    {
        var handler = new FakeHttpMessageHandler(EmbeddingsResponse([1f, 2f, 3f], [4f, 5f, 6f]));
        var client = new EmbeddingClient(new HttpClient(handler));

        var result = await client.EmbedAsync(Config(), ["alpha", "beta"]);

        Assert.Equal(2, result.Count);
        Assert.Equal([1f, 2f, 3f], result[0]);
        Assert.Equal([4f, 5f, 6f], result[1]);
        Assert.Equal("http://llm.test/v1/embeddings", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-emb", handler.LastRequest.Headers.Authorization!.ToString());
        Assert.Contains("nomic-embed-text", handler.LastRequestBody);
        Assert.Contains("alpha", handler.LastRequestBody);
        Assert.Contains("beta", handler.LastRequestBody);
    }

    [Fact]
    public async Task EmbedAsync_OrdersByIndex_WhenResponseOutOfOrder()
    {
        // The provider may return data out of order; results must line up with the input order via "index".
        var payload = JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { index = 1, embedding = new[] { 9f, 9f, 9f } },
                new { index = 0, embedding = new[] { 1f, 1f, 1f } },
            },
        });
        var client = new EmbeddingClient(new HttpClient(new FakeHttpMessageHandler(payload)));

        var result = await client.EmbedAsync(Config(), ["first", "second"]);

        Assert.Equal([1f, 1f, 1f], result[0]);
        Assert.Equal([9f, 9f, 9f], result[1]);
    }

    [Fact]
    public async Task EmbedAsync_EmptyInput_ReturnsEmpty_WithoutCallingEndpoint()
    {
        var handler = new FakeHttpMessageHandler(EmbeddingsResponse());
        var client = new EmbeddingClient(new HttpClient(handler));

        var result = await client.EmbedAsync(Config(), []);

        Assert.Empty(result);
        Assert.Null(handler.LastRequest); // no HTTP call for an empty batch
    }

    [Fact]
    public async Task EmbedAsync_Batches_WhenInputsExceedBatchSize()
    {
        // With batch size 2 and 3 inputs, two requests are made; the last one carries the remainder.
        var responses = new Queue<string>(
        [
            EmbeddingsResponse([1f, 1f, 1f], [2f, 2f, 2f]),
            EmbeddingsResponse([3f, 3f, 3f]),
        ]);
        var handler = new QueuedHttpMessageHandler(responses);
        var client = new EmbeddingClient(new HttpClient(handler));

        var result = await client.EmbedAsync(Config(batchSize: 2), ["a", "b", "c"]);

        Assert.Equal(3, result.Count);
        Assert.Equal([3f, 3f, 3f], result[2]);
        Assert.Equal(2, handler.Requests.Count); // two batched calls
    }
}
