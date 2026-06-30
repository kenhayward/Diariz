using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Tools;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class ChatToolSettingsResolverTests
{
    [Fact]
    public void ParseDisabled_SplitsCsv()
    {
        var set = ChatToolSettingsResolver.ParseDisabled(" a, b ,c ");
        Assert.Equal(new[] { "a", "b", "c" }, set.OrderBy(x => x));
        Assert.Empty(ChatToolSettingsResolver.ParseDisabled(null));
    }

    [Fact]
    public void ParseOverrides_ReadsMap_IgnoresInvalid()
    {
        Assert.True(ChatToolSettingsResolver.ParseOverrides("{\"who_said_that\":true}")["who_said_that"]);
        Assert.Empty(ChatToolSettingsResolver.ParseOverrides("not json"));
        Assert.Empty(ChatToolSettingsResolver.ParseOverrides(null));
    }

    private static ChatToolSettingsResolver Build(Diariz.Domain.DiarizDbContext db, ChatOptions opts) =>
        new(db, new ChatToolRegistry([new StubChatTool("who_said_that"), new StubChatTool("list_recordings")]),
            Options.Create(opts));

    [Fact]
    public async Task NoUserSettings_UsesServerDefaults()
    {
        using var db = TestDb.Create();
        var r = await Build(db, new ChatOptions { ToolsEnabled = true }).ResolveAsync(Guid.NewGuid());

        Assert.True(r.MasterEnabled);
        Assert.Equal(2, r.ActiveTools.Count); // both on by default
        Assert.All(r.Catalog, c => Assert.True(c.Enabled));
    }

    [Fact]
    public async Task ServerMasterOff_NoActiveTools_ButCatalogStillResolved()
    {
        using var db = TestDb.Create();
        var r = await Build(db, new ChatOptions { ToolsEnabled = false }).ResolveAsync(Guid.NewGuid());

        Assert.False(r.MasterEnabled);
        Assert.Empty(r.ActiveTools);
        Assert.All(r.Catalog, c => Assert.True(c.Enabled)); // per-tool default still on
    }

    [Fact]
    public async Task ServerDisabledTool_IsOffByDefault()
    {
        using var db = TestDb.Create();
        var r = await Build(db, new ChatOptions { ToolsEnabled = true, DisabledTools = "list_recordings" })
            .ResolveAsync(Guid.NewGuid());

        Assert.Single(r.ActiveTools);
        Assert.Equal("who_said_that", r.ActiveTools[0].Name);
        Assert.False(r.Catalog.First(c => c.Name == "list_recordings").DefaultEnabled);
    }

    [Fact]
    public async Task UserOverrides_WinOverDefaults()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings
        {
            UserId = userId,
            ChatToolsEnabled = true,
            ChatToolOverridesJson = "{\"who_said_that\":false}", // user turned this one off
        });
        await db.SaveChangesAsync();

        var r = await Build(db, new ChatOptions { ToolsEnabled = false }).ResolveAsync(userId);

        Assert.True(r.MasterEnabled); // user master override beats server
        Assert.Single(r.ActiveTools);
        Assert.Equal("list_recordings", r.ActiveTools[0].Name);
        Assert.False(r.Catalog.First(c => c.Name == "who_said_that").Enabled);
    }
}
