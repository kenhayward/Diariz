using System;
using System.Text.Json;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Tools;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The send_email chat tool: it emails the SIGNED-IN user's own registered address only.</summary>
public class SendEmailToolTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Guid SeedUser(DiarizDbContext db, string? email, string? name = "Alice Smith")
    {
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, Email = email, UserName = email ?? "u", FullName = name });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Execute_SendsToTheUsersRegisteredEmail_WithSubjectAndBody()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db, "me@example.com");
        var email = new FakeEmailSender();

        var result = await new SendEmailTool(db, email).ExecuteAsync(
            Args("""{"subject":"Follow-ups","body":"Hi,\n\nHere are the actions.\n\nThanks"}"""),
            new ChatToolContext(me, []), default);

        var msg = Assert.Single(email.Messages);
        Assert.Equal("me@example.com", msg.To);
        Assert.Equal("Follow-ups", msg.Subject);
        Assert.Contains("Here are the actions", msg.Body);
        Assert.Contains("me@example.com", result); // confirmation the model can relay
    }

    [Fact]
    public async Task Execute_RendersMarkdownToHtml_IncludingGfmTables()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db, "me@example.com");
        var email = new FakeEmailSender();

        await new SendEmailTool(db, email).ExecuteAsync(
            Args("""{"subject":"S","body":"**Bold**\n\n| A | B |\n|---|---|\n| 1 | 2 |"}"""),
            new ChatToolContext(me, []), default);

        var body = Assert.Single(email.Messages).Body;
        Assert.Contains("<strong>Bold</strong>", body);   // markdown converted to HTML
        Assert.Contains("<table", body);                  // GitHub-flavoured table rendered
        Assert.DoesNotContain("| A | B |", body);          // no raw markdown left over
    }

    [Fact]
    public async Task Execute_IgnoresAnyRecipientInArgs_AlwaysUsesTheOwnersAddress()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db, "me@example.com");
        var email = new FakeEmailSender();

        await new SendEmailTool(db, email).ExecuteAsync(
            Args("""{"to":"attacker@evil.com","subject":"S","body":"B"}"""),
            new ChatToolContext(me, []), default);

        // The injected recipient is ignored — it can only ever reach the owner.
        Assert.Equal("me@example.com", Assert.Single(email.Messages).To);
    }

    [Fact]
    public async Task Execute_MissingSubjectOrBody_DoesNotSend_ReturnsError()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db, "me@example.com");
        var email = new FakeEmailSender();

        var noSubject = await new SendEmailTool(db, email).ExecuteAsync(Args("""{"body":"B"}"""), new ChatToolContext(me, []), default);
        var noBody = await new SendEmailTool(db, email).ExecuteAsync(Args("""{"subject":"S"}"""), new ChatToolContext(me, []), default);

        Assert.Empty(email.Messages);
        Assert.Contains("subject", noSubject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("body", noBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_WhenEmailNotConfigured_ReturnsNotConfigured()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db, "me@example.com");
        var email = new FakeEmailSender { Sent = false }; // server email disabled

        var result = await new SendEmailTool(db, email).ExecuteAsync(
            Args("""{"subject":"S","body":"B"}"""), new ChatToolContext(me, []), default);

        Assert.Contains("configured", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_WhenUserHasNoEmailOnRecord_ReturnsError_DoesNotSend()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db, email: null);
        var email = new FakeEmailSender();

        var result = await new SendEmailTool(db, email).ExecuteAsync(
            Args("""{"subject":"S","body":"B"}"""), new ChatToolContext(me, []), default);

        Assert.Empty(email.Messages);
        Assert.Contains("no email", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Schema_HasNoRecipientField()
    {
        var schema = JsonSerializer.Serialize(new SendEmailTool(null!, null!).ParametersSchema);
        Assert.DoesNotContain("\"to\"", schema);       // there is no way to address anyone else
        Assert.Contains("subject", schema);
        Assert.Contains("body", schema);
    }
}
