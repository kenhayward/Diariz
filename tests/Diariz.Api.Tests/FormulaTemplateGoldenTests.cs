using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>The regression bar for making formulas structured: <b>an existing formula must produce byte-identical
/// output</b>. A formula used to be a bare prompt run as the system message with the context as the user message.
/// It is now a template - but a bare prompt is stored as one headless (level-0) section holding one prompt block,
/// which composes to exactly one LLM call with that prompt and no heading around it.
///
/// These tests pin that. If someone later "simplifies" the composer, or drops level-0 sections, or routes formulas
/// through the minutes strategies (which would wrap them in the minutes guardrails and a [[WRITE:]] skeleton), the
/// output of every formula every user has saved would change - and these fail.</summary>
public class FormulaTemplateGoldenTests
{
    private static async Task<Recording> SeedRecording(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "R", BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 1000, Original = "We agreed to ship on Friday.", Ordinal = 0,
        });
        await db.SaveChangesAsync();
        return rec;
    }

    /// <summary>Seeds the pre-created Generating row the async run flips, and drives the real ProcessAsync path.</summary>
    private static async Task<string> RunAsync(DiarizDbContext db, FakeChatStreamClient chat, Formula formula, Guid recordingId)
    {
        var result = new FormulaResult
        {
            Id = Guid.NewGuid(), RecordingId = recordingId, FormulaId = formula.Id,
            Name = formula.Name, Ordinal = 0, Status = FormulaRunStatus.Generating,
        };
        db.Formulas.Add(formula);
        db.FormulaResults.Add(result);
        await db.SaveChangesAsync();

        await FormulaRunProcessor.ProcessAsync(
            db, chat, new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            new FormulaRunJob(recordingId, null, result.Id, formula.Id, Guid.NewGuid()),
            48_000, NullLogger.Instance, new CapturingWebhookPublisher(), "");

        var reloaded = await db.FormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Ready, reloaded!.Status);
        return reloaded.Text;
    }

    /// <summary>Every Diariz formula that ships in the box (the <c>formulas/*.md</c> files).</summary>
    public static TheoryData<string> BuiltInPrompts() =>
    [
        "Read the meeting context provided and produce two Markdown sections:",
        "Draft a follow-up email to the attendees.",
        "Write a short, shareable recap of the meeting.",
        "Read the transcript and give a read on the emotional tone",
    ];

    // The heart of it: a bare prompt makes ONE call, with the prompt verbatim as the system message - exactly as
    // before formulas had templates. No preamble, no skeleton, no "MEETING CONTEXT" wrapper.
    [Theory]
    [MemberData(nameof(BuiltInPrompts))]
    public async Task A_bare_prompt_formula_still_makes_one_call_with_its_prompt_as_the_system_message(string prompt)
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var chat = new FakeChatStreamClient { Tokens = ["The ", "answer."] };

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Diariz, Name = "F",
            ContentJson = TemplateContent.FromPrompt(prompt).Serialize(),
            Context = FormulaContext.Transcript, Enabled = true, IsBuiltIn = true,
        };

        var text = await RunAsync(db, chat, formula, rec.Id);

        Assert.Equal(1, chat.Calls);
        var messages = Assert.Single(chat.AllStreamMessages);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal(prompt, messages[0].Content); // verbatim - not wrapped, not decorated
        Assert.Equal("user", messages[1].Role);
        Assert.Contains("We agreed to ship on Friday.", messages[1].Content);

        // And the output is the model's answer, with nothing added around it - no heading crept in.
        Assert.Equal("The answer.", text);
    }

    [Fact]
    public async Task A_bare_prompt_formula_output_carries_no_heading()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid());
        var chat = new FakeChatStreamClient { Tokens = ["Just prose."] };
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, Name = "F",
            ContentJson = TemplateContent.FromPrompt("Summarise.").Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };

        var text = await RunAsync(db, chat, formula, rec.Id);

        Assert.DoesNotContain("#", text);
        Assert.Equal("Just prose.", text);
    }

    // The other half of the bar: the seeded minutes templates are all headed sections, so the level-0 branch added
    // to the composer cannot reach them. If a future seed introduces a level-0 section this fails, which is the
    // prompt to re-check the minutes output.
    [Fact]
    public void Every_seeded_meeting_template_uses_headed_sections_so_the_headless_branch_cannot_affect_minutes()
    {
        foreach (var std in MeetingTypeSeeder.Standards)
        {
            var content = TemplateContent.Parse(std.ContentJson);
            Assert.NotEmpty(content.Sections);
            Assert.All(content.Sections, s => Assert.True(s.Level >= 1, $"{std.Key}: section '{s.Title}' is level {s.Level}"));
        }
    }

    // A structured formula (one a user builds once the editor lands) composes like a document: headings and
    // boilerplate emitted verbatim, one LLM call per prompt block.
    [Fact]
    public async Task A_structured_formula_composes_its_headings_and_calls_once_per_prompt_block()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid());
        var chat = new FakeChatStreamClient { StreamRounds = ["First.", "Second."] };

        var content = new TemplateContent(
        [
            new TemplateSection(1, "Decisions", [new TemplateBlock(TemplateBlock.Prompt, Text: "List decisions.")]),
            new TemplateSection(1, "Risks", [new TemplateBlock(TemplateBlock.Prompt, Text: "List risks.")]),
        ]);
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, Name = "F",
            ContentJson = content.Serialize(), Context = FormulaContext.Transcript, Enabled = true,
        };

        var text = await RunAsync(db, chat, formula, rec.Id);

        Assert.Equal(2, chat.Calls);
        Assert.Equal("# Decisions\n\nFirst.\n\n# Risks\n\nSecond.", text);
    }
}
