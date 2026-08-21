using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests.Llm;

/// <summary>Which models a chat user may pick, and what happens to a pick that is not offered.</summary>
public class ChatModelCatalogTests
{
    private static LlmModel Seed(DiarizDbContext db, string name, bool chatEnabled = false,
        int contextLength = 8192, string? displayName = null)
    {
        var m = new LlmModel
        {
            Id = Guid.NewGuid(), Name = name, DisplayName = displayName, ApiBase = "http://llm/v1",
            ContextLength = contextLength, ChatEnabled = chatEnabled,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmModels.Add(m);
        db.SaveChanges();
        return m;
    }

    private static void AssignChat(DiarizDbContext db, Guid modelId)
    {
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = modelId });
        db.SaveChanges();
    }

    [Fact]
    public async Task Offers_the_chat_assigned_model_even_when_its_flag_is_off()
    {
        // The default must always be selectable: it is the model actually serving the conversation, and a
        // picker that excluded it could show no current selection at all.
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model", chatEnabled: false);
        AssignChat(db, chat.Id);

        var options = await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).ListAsync();

        var only = Assert.Single(options);
        Assert.Equal(chat.Id, only.Id);
        Assert.True(only.IsDefault);
    }

    [Fact]
    public async Task Lists_the_default_first_then_enabled_models_by_label()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model");
        Seed(db, "zeta", chatEnabled: true);
        Seed(db, "alpha", chatEnabled: true, displayName: "Alpha");
        AssignChat(db, chat.Id);

        var options = await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).ListAsync();

        Assert.Equal(["chat-model", "Alpha", "zeta"], options.Select(o => o.Label));
        Assert.Equal([true, false, false], options.Select(o => o.IsDefault));
    }

    [Fact]
    public async Task Does_not_list_a_model_that_is_not_offered()
    {
        // The counterpart to ResolveOfferedAsync's refusal. Without it the picker would advertise a model
        // the resolver then declines to use, which reads to the user as the choice being ignored.
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model");
        Seed(db, "expensive-cloud-model", chatEnabled: false);
        AssignChat(db, chat.Id);

        var options = await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).ListAsync();

        Assert.Equal(["chat-model"], options.Select(o => o.Name));
    }

    [Fact]
    public async Task Falls_back_to_the_platform_default_when_chat_has_no_assignment()
    {
        using var db = TestDb.Create();
        var fallback = Seed(db, "platform-default");
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, DefaultLlmModelId = fallback.Id,
        });
        await db.SaveChangesAsync();

        Assert.Equal(fallback.Id, await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).DefaultModelIdAsync());
    }

    [Fact]
    public async Task Prefers_the_chat_assignment_over_the_platform_default()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model");
        var other = Seed(db, "other-model");
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, DefaultLlmModelId = other.Id,
        });
        await db.SaveChangesAsync();
        AssignChat(db, chat.Id);

        Assert.Equal(chat.Id, await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).DefaultModelIdAsync());
    }

    [Fact]
    public async Task Resolves_an_offered_model_to_itself()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model");
        var big = Seed(db, "big-model", chatEnabled: true);
        AssignChat(db, chat.Id);

        Assert.Equal(big.Id, await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).ResolveOfferedAsync(big.Id));
    }

    [Fact]
    public async Task Ignores_a_model_that_is_not_offered()
    {
        // The security boundary: without this, any signed-in user could route their chat at any endpoint
        // the platform has configured, by posting an id they were never meant to have.
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model");
        var secret = Seed(db, "expensive-cloud-model", chatEnabled: false);
        AssignChat(db, chat.Id);

        Assert.Null(await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).ResolveOfferedAsync(secret.Id));
    }

    [Fact]
    public async Task Ignores_an_unknown_id()
    {
        using var db = TestDb.Create();
        Assert.Null(await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).ResolveOfferedAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Resolves_the_chat_default_even_though_its_flag_is_off()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model", chatEnabled: false);
        AssignChat(db, chat.Id);

        Assert.Equal(chat.Id, await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).ResolveOfferedAsync(chat.Id));
    }

    [Fact]
    public async Task Resolves_null_for_no_request()
    {
        using var db = TestDb.Create();
        Assert.Null(await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).ResolveOfferedAsync(null));
    }

    [Fact]
    public async Task Lists_nothing_when_the_platform_has_no_models()
    {
        // The environment-fallback config has no row, so there is genuinely nothing to offer. The picker
        // renders an empty list rather than inventing an entry for a model it cannot name.
        using var db = TestDb.Create();
        Assert.Empty(await new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())).ListAsync());
    }

    // ---- SupportsImages: the flag the chat picker gates image attachments on ----

    private static void SetParameters(DiarizDbContext db, LlmModel model, LlmCallGroup group, string json)
    {
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = group, ParametersJson = json,
        });
        db.SaveChanges();
    }

    private static ChatModelCatalog Catalog(DiarizDbContext db, LlmDefaultsOptions? defaults = null) =>
        new(db, Options.Create(defaults ?? new LlmDefaultsOptions()));

    [Fact]
    public async Task SupportsImages_ComesFromTheChatOverride()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "vision-model");
        AssignChat(db, chat.Id);
        SetParameters(db, chat, LlmCallGroup.Chat, """{"images_supported":true}""");

        Assert.True(Assert.Single(await Catalog(db).ListAsync()).SupportsImages);
    }

    [Fact]
    public async Task SupportsImages_FallsThroughToTheModelBaseLayer()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "vision-model");
        AssignChat(db, chat.Id);
        SetParameters(db, chat, LlmCallGroup.ModelBase, """{"images_supported":true}""");

        Assert.True(Assert.Single(await Catalog(db).ListAsync()).SupportsImages);
    }

    [Fact]
    public async Task SupportsImages_IsFalseWhenNoLayerMentionsIt()
    {
        // The shipped default. A model nobody has told us about cannot see, which is the safe direction:
        // the gate refuses rather than sending images into a text-only endpoint.
        using var db = TestDb.Create();
        var chat = Seed(db, "text-model");
        AssignChat(db, chat.Id);

        Assert.False(Assert.Single(await Catalog(db).ListAsync()).SupportsImages);
    }

    [Fact]
    public async Task SupportsImages_ChatOverrideOfFalseBeatsAModelBaseTrue()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "vision-model");
        AssignChat(db, chat.Id);
        SetParameters(db, chat, LlmCallGroup.ModelBase, """{"images_supported":true}""");
        SetParameters(db, chat, LlmCallGroup.Chat, """{"images_supported":false}""");

        Assert.False(Assert.Single(await Catalog(db).ListAsync()).SupportsImages);
    }

    [Fact]
    public async Task SupportsImages_HonoursThePlatformDefaultLayer()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "text-model");
        AssignChat(db, chat.Id);

        var defaults = new LlmDefaultsOptions { ImagesSupported = true };

        Assert.True(Assert.Single(await Catalog(db, defaults).ListAsync()).SupportsImages);
    }

    /// <summary>The picker and the pipeline must not merely happen to agree.
    ///
    /// <para>This is what makes <see cref="LlmParameterStack"/> load-bearing rather than incidental. Without
    /// it, the catalogue could grow its own layer walk, pass every test above, and still offer a model as
    /// vision-capable that <see cref="LlmSettingsResolver"/> then rejects as text-only - a defect visible
    /// only in production, in which neither code path looks wrong.</para></summary>
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("""{"images_supported":true}""", null, true)]
    [InlineData(null, """{"images_supported":true}""", true)]
    [InlineData("""{"images_supported":false}""", """{"images_supported":true}""", false)]
    public async Task SupportsImages_AgreesWithWhatTheSettingsResolverWillSend(
        string? chatLayer, string? baseLayer, bool expected)
    {
        using var db = TestDb.Create();
        var model = Seed(db, "some-model");
        AssignChat(db, model.Id);
        if (chatLayer is not null) SetParameters(db, model, LlmCallGroup.Chat, chatLayer);
        if (baseLayer is not null) SetParameters(db, model, LlmCallGroup.ModelBase, baseLayer);

        var defaults = new LlmDefaultsOptions();
        var catalog = Catalog(db, defaults);
        var offered = Assert.Single(await catalog.ListAsync());

        var resolver = new LlmSettingsResolver(
            db, Options.Create(defaults),
            Options.Create(new SummarizationOptions { ApiBase = "http://env/v1", Model = "env" }),
            new FakeApiKeyProtector(), catalog, Options.Create(new ChatOptions()));
        var cfg = await resolver.ResolveAsync(LlmCallKind.ChatMessage, model.Id);

        Assert.Equal(expected, offered.SupportsImages);
        Assert.Equal(cfg.Parameters.ImagesSupported, offered.SupportsImages);
    }
}
