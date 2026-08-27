using Diariz.Api.Services.Llm;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The synchronous run pipeline: load a Formula + a recording scoped to the caller, enforce
/// run-access by <see cref="FormulaScope"/>, build the context (<see cref="FormulaContextBuilder"/>),
/// call the LLM, and persist a <see cref="FormulaResult"/>.</summary>
public class FormulaRunnerTests
{
    private static async Task<(Recording rec, Transcription tr)> SeedRecordingWithTranscript(
        DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "R", BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 1000, Original = "Hello there, team.", Ordinal = 0,
        });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    private static FormulaRunner MakeRunner(
        DiarizDbContext db, FakeChatStreamClient chat, FakeLlmSettingsResolver resolver) =>
        new(db, chat, resolver);

    [Fact]
    public async Task RunAsync_PersonalFormulaOwnedByCaller_PersistsResultAndSendsPromptAndTranscript()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "Key Decisions", ContentJson = TemplateContent.FromPrompt("Summarize the key decisions made.").Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var chat = new FakeChatStreamClient { Tokens = ["# Key", " Decisions\n", "- Did the thing"] };
        var resolver = new FakeLlmSettingsResolver();
        var runner = MakeRunner(db, chat, resolver);

        var result = await runner.RunAsync(userId, rec.Id, formula.Id);

        Assert.Equal(rec.Id, result.RecordingId);
        Assert.Equal(userId, result.CreatedByUserId);
        Assert.Equal(formula.Id, result.FormulaId);
        Assert.Equal("Key Decisions", result.Name);
        Assert.Equal("# Key Decisions\n- Did the thing", result.Text);

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal(result.Text, persisted!.Text);

        Assert.NotNull(chat.LastMessages);
        Assert.Equal(2, chat.LastMessages!.Count);
        Assert.Equal("system", chat.LastMessages[0].Role);
        Assert.Equal("Summarize the key decisions made.", chat.LastMessages[0].Content);
        Assert.Equal("user", chat.LastMessages[1].Role);
        Assert.Contains("Hello there, team.", chat.LastMessages[1].Content);
    }

    [Fact]
    // Re-running the SAME formula replaces its result rather than appending a duplicate - and keeps the row's id
    // and position, so the list doesn't reshuffle and an open link to the result stays valid.
    public async Task RunAsync_RunningTheSameFormulaAgain_ReplacesItsResult()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "F", ContentJson = TemplateContent.FromPrompt("P").Serialize(), Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver());

        var first = await runner.RunAsync(userId, rec.Id, formula.Id);
        var second = await runner.RunAsync(userId, rec.Id, formula.Id);

        Assert.Equal(first.Id, second.Id);          // the same document, re-generated
        Assert.Equal(0, second.Ordinal);            // it did not move
        Assert.Single(db.FormulaResults.Where(r => r.RecordingId == rec.Id).ToList());
    }

    [Fact]
    public async Task RunAsync_DifferentFormulas_EachGetTheirOwnResult()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        Formula Make(string name) => new()
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = name, ContentJson = TemplateContent.FromPrompt("P").Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };
        var a = Make("A");
        var b = Make("B");
        db.Formulas.AddRange(a, b);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver());

        var first = await runner.RunAsync(userId, rec.Id, a.Id);
        var second = await runner.RunAsync(userId, rec.Id, b.Id);

        Assert.Equal(0, first.Ordinal);
        Assert.Equal(1, second.Ordinal);
        Assert.Equal(2, db.FormulaResults.Count(r => r.RecordingId == rec.Id));
    }

    [Fact]
    public async Task RunAsync_PersonalFormulaOwnedByAnotherUser_ThrowsFormulaNotFoundException()
    {
        // A non-owned Personal formula is treated as "not found" rather than "access denied" so its very
        // existence isn't leaked - a disabled Platform/Diariz formula (public knowledge) still throws Access.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = otherUserId,
            Name = "Not Mine", ContentJson = TemplateContent.FromPrompt("P").Serialize(), Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver());

        await Assert.ThrowsAsync<FormulaNotFoundException>(() => runner.RunAsync(userId, rec.Id, formula.Id));
    }

    [Fact]
    public async Task RunAsync_DisabledPlatformFormula_ThrowsFormulaAccessException()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Platform,
            Name = "Disabled", ContentJson = TemplateContent.FromPrompt("P").Serialize(), Context = FormulaContext.Transcript, Enabled = false,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver());

        await Assert.ThrowsAsync<FormulaAccessException>(() => runner.RunAsync(userId, rec.Id, formula.Id));
    }

    [Fact]
    public async Task RunAsync_RecordingNotOwnedByCaller_ThrowsFormulaNotFoundException()
    {
        using var db = TestDb.Create();
        var ownerId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, ownerId);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = callerId,
            Name = "F", ContentJson = TemplateContent.FromPrompt("P").Serialize(), Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver());

        await Assert.ThrowsAsync<FormulaNotFoundException>(() => runner.RunAsync(callerId, rec.Id, formula.Id));
    }

    [Fact]
    public async Task RunAsync_UnknownFormulaId_ThrowsFormulaNotFoundException()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver());

        await Assert.ThrowsAsync<FormulaNotFoundException>(
            () => runner.RunAsync(userId, rec.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task RunAsync_UnknownRecordingId_ThrowsFormulaNotFoundException()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Diariz,
            Name = "F", ContentJson = TemplateContent.FromPrompt("P").Serialize(), Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver());

        await Assert.ThrowsAsync<FormulaNotFoundException>(
            () => runner.RunAsync(userId, Guid.NewGuid(), formula.Id));
    }

    [Fact]
    public async Task RunAsync_SharedFormulaSubscribedByCaller_ProducesResult()
    {
        // B runs A's shared Personal formula over B's own recording; a subscription grants run access.
        using var db = TestDb.Create();
        var ownerA = Guid.NewGuid();
        var callerB = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, callerB);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = ownerA,
            Name = "A's Shared", ContentJson = TemplateContent.FromPrompt("P").Serialize(), Context = FormulaContext.Transcript, Enabled = true, Shared = true,
        };
        db.Formulas.Add(formula);
        db.FormulaSubscriptions.Add(new FormulaSubscription
        {
            Id = Guid.NewGuid(), FormulaId = formula.Id, UserId = callerB, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient { Tokens = ["ok"] }, new FakeLlmSettingsResolver());

        var result = await runner.RunAsync(callerB, rec.Id, formula.Id);

        Assert.Equal(callerB, result.CreatedByUserId);
        Assert.Equal(formula.Id, result.FormulaId);
    }

    [Fact]
    public async Task RunAsync_SharedFormulaNotSubscribed_ThrowsFormulaNotFoundException()
    {
        using var db = TestDb.Create();
        var ownerA = Guid.NewGuid();
        var callerB = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, callerB);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = ownerA,
            Name = "A's Shared", ContentJson = TemplateContent.FromPrompt("P").Serialize(), Context = FormulaContext.Transcript, Enabled = true, Shared = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver());

        await Assert.ThrowsAsync<FormulaNotFoundException>(() => runner.RunAsync(callerB, rec.Id, formula.Id));
    }

    [Fact]
    public async Task RunAsync_NonSharedNonOwnedPersonalFormula_ThrowsFormulaNotFoundException()
    {
        using var db = TestDb.Create();
        var ownerA = Guid.NewGuid();
        var callerB = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, callerB);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = ownerA,
            Name = "A's Private", ContentJson = TemplateContent.FromPrompt("P").Serialize(), Context = FormulaContext.Transcript, Enabled = true, Shared = false,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver());

        await Assert.ThrowsAsync<FormulaNotFoundException>(() => runner.RunAsync(callerB, rec.Id, formula.Id));
    }

    [Fact]
    // The synchronous run path (chat's run_formula tool, and - with no enclosing scope at all - the MCP
    // run_formula tool) must attribute its own LLM call rather than inheriting whatever happens to be
    // ambient. Asserted from INSIDE the fake chat client's onCall, at the moment the call is actually made -
    // LlmCallScope.Active read after RunAsync returns would prove nothing, since Dispose has already run.
    public async Task RunAsync_AttributesTheCallToTheRecordingAndItsOwner()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        db.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = "owner@example.com", Email = "owner@example.com",
        });
        await db.SaveChangesAsync();

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "F", ContentJson = TemplateContent.FromPrompt("P").Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        LlmCallKind? observedKind = null;
        Guid? observedUser = null;
        Guid? observedRecording = null;
        string? observedEmail = null;
        var chat = new FakeChatStreamClient(onCall: () =>
        {
            observedKind = LlmCallScope.Active?.Kind;
            observedUser = LlmCallScope.Active?.UserId;
            observedRecording = LlmCallScope.Active?.RecordingId;
            observedEmail = LlmCallScope.Active?.UserEmail;
        });
        var runner = MakeRunner(db, chat, new FakeLlmSettingsResolver());

        await runner.RunAsync(userId, rec.Id, formula.Id);

        Assert.Equal(LlmCallKind.FormulaRun, observedKind);
        Assert.Equal(userId, observedUser);
        Assert.Equal(rec.Id, observedRecording);
        Assert.Equal("owner@example.com", observedEmail);
    }

    [Fact]
    public async Task RunAsync_NotConfigured_ThrowsFormulaNotConfiguredException()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "F", ContentJson = TemplateContent.FromPrompt("P").Serialize(), Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var resolver = new FakeLlmSettingsResolver
        {
            Config = new LlmRequestConfig("", "", "", new LlmParameters { TimeoutSeconds = 60 }), // empty ApiBase => disabled
        };
        var runner = MakeRunner(db, new FakeChatStreamClient(), resolver);

        await Assert.ThrowsAsync<FormulaNotConfiguredException>(() => runner.RunAsync(userId, rec.Id, formula.Id));
    }

    // ---- $USERNAME substitution ----
    //
    // These drive the real run path rather than calling the name-resolution helper directly: it is internal,
    // this repo has no InternalsVisibleTo, and asserting on the system message the model actually received
    // proves the wiring as well as the resolution order. A helper-only test could not.

    /// <summary>$USERNAME reaches the model already substituted, with the name taken from the linked person -
    /// the name the transcript itself shows.</summary>
    [Fact]
    public async Task RunAsync_SubstitutesTheLinkedPersonsNameIntoThePrompt()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        db.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Display Name",
        });
        db.People.Add(new Person { Id = Guid.NewGuid(), LinkedUserId = userId, Name = "Ada Lovelace" });
        var formula = await SeedPromptFormula(db, userId, "What role did $USERNAME play in this meeting?");

        var chat = new FakeChatStreamClient();
        await MakeRunner(db, chat, new FakeLlmSettingsResolver()).RunAsync(userId, rec.Id, formula.Id);

        Assert.Equal("What role did Ada Lovelace play in this meeting?", chat.LastMessages![0].Content);
    }

    /// <summary>No directory entry (an account that predates it) falls back to the display name.</summary>
    [Fact]
    public async Task RunAsync_FallsBackToTheDisplayName_WhenThereIsNoLinkedPerson()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        db.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Display Name",
        });
        var formula = await SeedPromptFormula(db, userId, "Ask $USERNAME");

        var chat = new FakeChatStreamClient();
        await MakeRunner(db, chat, new FakeLlmSettingsResolver()).RunAsync(userId, rec.Id, formula.Id);

        Assert.Equal("Ask Display Name", chat.LastMessages![0].Content);
    }

    /// <summary>An invited account with no name yet falls back to the email rather than substituting
    /// nothing.</summary>
    [Fact]
    public async Task RunAsync_FallsBackToTheEmail_WhenTheAccountHasNoName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test" });
        var formula = await SeedPromptFormula(db, userId, "Ask $USERNAME");

        var chat = new FakeChatStreamClient();
        await MakeRunner(db, chat, new FakeLlmSettingsResolver()).RunAsync(userId, rec.Id, formula.Id);

        Assert.Equal("Ask a@b.test", chat.LastMessages![0].Content);
    }

    /// <summary>Somebody else's directory entry must not be picked up. The column beside LinkedUserId
    /// (CreatedByUserId, mapped to "UserId") records who ENROLLED a person and means something else
    /// entirely.</summary>
    [Fact]
    public async Task RunAsync_IgnoresAPersonLinkedToSomeoneElse()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        db.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Display Name",
        });
        db.People.Add(new Person
        {
            Id = Guid.NewGuid(), LinkedUserId = Guid.NewGuid(), CreatedByUserId = userId, Name = "Somebody Else",
        });
        var formula = await SeedPromptFormula(db, userId, "Ask $USERNAME");

        var chat = new FakeChatStreamClient();
        await MakeRunner(db, chat, new FakeLlmSettingsResolver()).RunAsync(userId, rec.Id, formula.Id);

        Assert.Equal("Ask Display Name", chat.LastMessages![0].Content);
    }

    /// <summary>A run must never write to the people directory as a side effect - which is why the name is
    /// looked up with a plain query and not IPeopleDirectory.EnsureForUserAsync, which mints a person.</summary>
    [Fact]
    public async Task RunAsync_DoesNotProvisionAPersonForTheRunningUser()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        db.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Display Name",
        });
        var formula = await SeedPromptFormula(db, userId, "Ask $USERNAME");

        await MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver())
            .RunAsync(userId, rec.Id, formula.Id);

        Assert.Empty(db.People);
    }

    /// <summary>Literal text is substituted too, so a token in boilerplate does not survive into the produced
    /// document looking like a bug.</summary>
    [Fact]
    public async Task RunAsync_SubstitutesTheUserNameIntoBoilerplate()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        db.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Ada Lovelace",
        });

        var content = new TemplateContent([
            new TemplateSection(1, "Report", [
                new TemplateBlock(TemplateBlock.Boilerplate, Text: "Prepared for $USERNAME."),
            ]),
        ]);
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId, Name = "Report",
            ContentJson = content.Serialize(), Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var result = await MakeRunner(db, new FakeChatStreamClient(), new FakeLlmSettingsResolver())
            .RunAsync(userId, rec.Id, formula.Id);

        Assert.Contains("Prepared for Ada Lovelace.", result.Text);
    }

    private static async Task<Formula> SeedPromptFormula(DiarizDbContext db, Guid userId, string prompt)
    {
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId, Name = "Token",
            ContentJson = TemplateContent.FromPrompt(prompt).Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();
        return formula;
    }
}
