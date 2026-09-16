using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// <c>POST api/recordings/{id}/actions/live</c> through the real HTTP pipeline. The controller-direct unit test
/// skips model binding and validation, and that is exactly where a blank line used to fail: a non-nullable
/// <c>Text</c> is implicitly <c>[Required]</c>, which rejects whitespace, so one blank line 400'd the whole
/// attach and every retry failed the same way.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LiveActionsHttpIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task CreateLive_WithABlankLine_SkipsItInsteadOfRejectingTheRequest()
    {
        using var factory = new DiarizWebAppFactory(fx);
        var userId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
            Users.Ensure(db, userId);
            db.Recordings.Add(new Recording { Id = recId, UserId = userId, Title = "R", BlobKey = "k" });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(userId));

        var response = await client.PostAsJsonAsync($"/api/recordings/{recId}/actions/live", new
        {
            actions = new object[]
            {
                new { text = "Book the room", actor = "Ada" },
                new { text = "   " },
                new { text = "" },
                new { text = (string?)null },
            },
        });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        var created = await response.Content.ReadFromJsonAsync<List<RecordingActionDto>>();
        Assert.Equal(["Book the room"], created!.Select(a => a.Text));

        await using var verify = fx.CreateDbContext();
        Assert.Equal(["Book the room"], await verify.RecordingActions.Where(a => a.RecordingId == recId).Select(a => a.Text).ToListAsync());
    }
}
