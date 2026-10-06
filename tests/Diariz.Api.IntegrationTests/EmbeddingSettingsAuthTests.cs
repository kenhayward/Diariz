using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>The <c>ManagePlatform</c> gate on <c>EmbeddingSettingsController</c> over the real pipeline, for the
/// reason <see cref="LlmModelsAuthTests"/> gives - plus one admin round trip, which is what proves the new
/// <c>PlatformSettings</c> columns exist under real Postgres.
///
/// The gate matters: the saved endpoint receives every chunk of every transcript on the platform, and its key is a
/// credential.</summary>
[Collection(IntegrationCollection.Name)]
public class EmbeddingSettingsAuthTests(ContainersFixture fx)
{
    private DiarizWebAppFactory NewFactory() => new(fx);

    private static async Task<Guid> SeedUserAsync(DiarizWebAppFactory factory, bool platformAdmin)
    {
        var id = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        Users.Ensure(db, id);
        if (platformAdmin) Perms.Grant(db, id, Perms.PlatformAdministrator);
        return id;
    }

    private static HttpClient AuthenticatedClient(DiarizWebAppFactory factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Issue(userId));
        return client;
    }

    [Fact]
    public async Task Get_IsForbidden_ForNonAdminUser()
    {
        using var factory = NewFactory();
        using var client = AuthenticatedClient(factory, await SeedUserAsync(factory, platformAdmin: false));

        var resp = await client.GetAsync("/api/admin/embedding");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Update_IsForbidden_ForNonAdminUser()
    {
        // Redirecting embeddings would send every transcript on the platform to a host of the caller's choosing.
        using var factory = NewFactory();
        using var client = AuthenticatedClient(factory, await SeedUserAsync(factory, platformAdmin: false));

        var resp = await client.PutAsJsonAsync(
            "/api/admin/embedding", new UpdateEmbeddingSettingsRequest("http://evil.test/v1", "sk-x"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Test_IsForbidden_ForNonAdminUser()
    {
        using var factory = NewFactory();
        using var client = AuthenticatedClient(factory, await SeedUserAsync(factory, platformAdmin: false));

        var resp = await client.PostAsync("/api/admin/embedding/test", null);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_CanSaveAndReadBack_TheEndpoint()
    {
        using var factory = NewFactory();
        using var client = AuthenticatedClient(factory, await SeedUserAsync(factory, platformAdmin: true));

        try
        {
            var put = await client.PutAsJsonAsync(
                "/api/admin/embedding", new UpdateEmbeddingSettingsRequest("http://emb.test/v1", "sk-1"));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var dto = await client.GetFromJsonAsync<EmbeddingSettingsDto>("/api/admin/embedding");
            Assert.Equal("Platform", dto!.Source);
            Assert.Equal("http://emb.test/v1", dto.SavedApiBase);
            Assert.True(dto.SavedHasApiKey);
        }
        finally
        {
            // The settings row is shared by every class in the collection; leave it as the others expect.
            await client.PutAsJsonAsync("/api/admin/embedding", new UpdateEmbeddingSettingsRequest(null, null));
        }
    }
}
