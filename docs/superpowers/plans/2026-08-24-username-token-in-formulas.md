# `$USERNAME` in formula templates Implementation Plan

> **Shipped in v0.247.0 (PR #600).** Merged retrospectively on 2026-08-27 as a design record; the checkboxes below are preserved as written and are not outstanding work. Names and email addresses in the example fixtures are invented placeholders.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a formula's template contain `$USERNAME` and have it replaced, at run time, with the running user's transcript name - so prompts like "What role did $USERNAME play in this meeting" work.

**Architecture:** A pure `PromptTokens` helper does the replacement, and a second pure function walks a parsed `TemplateContent` applying it to every section title and block text. The walk runs at the two places `FormulaRunProcessor` parses a formula's template, so prompt blocks and literal blocks agree for free. The name is looked up once per run from the caller's linked `Person` (falling back to the account's display name, then email) and threaded in as a plain string, keeping the composer helpers free of a directory dependency.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core; xUnit with the EF Core in-memory provider and the `TestSupport` fakes.

Source spec: [docs/superpowers/specs/2026-08-24-chat-panel-three-changes-design.md](../specs/2026-08-24-chat-panel-three-changes-design.md)

## Global Constraints

- **TDD is required.** Write the failing test, run it and see it fail, then write the minimal code.
- **A passing run has no errors or warnings.** In particular, use `[GeneratedRegex]` rather than `new Regex(...)` - this repo's analyzer settings surface `SYSLIB1045` otherwise, and `AttendeeFormatter`/`HtmlText` already set the pattern.
- **No em dashes or en dashes in user-facing text** (help content, release notes, UI strings). Plain hyphen `-`.
- **Help content is ASCII only** and carries a `title` / `summary` / `group` / `order` front-matter block. `apps/web/src/content/help/helpContent.test.ts` enforces both.
- **This is an enhancement, not a fix, so it needs no GitHub issue.**
- **`main` is branch-protected.** Branch, push, open a PR. Never commit to `main`, never merge locally.
- **Version bump:** functional enhancement, so Minor +1 and Build reset. Read `version.json` and apply the rule to what is actually there.
- **Do not use `git add -A`.** Stage explicit paths.
- **Deployment surface:** server redeploy only. No endpoint changes, so **no OpenAPI snapshot regeneration and no n8n regeneration** are needed for this PR.
- **`--filter "Name=X"` does not work in this repo** despite what `CLAUDE.md` says. Use `--filter "FullyQualifiedName~X"`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Diariz.Api/Services/PromptTokens.cs` (create) | Pure: the `$USERNAME` regex replacement, and the `TemplateContent` walk that applies it. |
| `src/Diariz.Api/Services/FormulaRunProcessor.cs` (modify) | Look up the transcript name; apply the walk at both `TemplateContent.Parse` sites; take the name as a parameter on the two run helpers. |
| `src/Diariz.Api/Services/FormulaRunner.cs` (modify) | Look up the name and pass it through on the synchronous path. |
| `tests/Diariz.Api.Tests/PromptTokensTests.cs` (create) | The pure helper's behaviour. |
| `tests/Diariz.Api.Tests/FormulaRunnerTests.cs` (modify) | End-to-end: the substituted name reaches the model. |
| `tests/Diariz.Api.Tests/FormulaRunProcessorTests.cs` (modify) | The async job path resolves from `job.UserId`. |
| `apps/web/src/content/help/en/formula-configuration.md` (modify) | Document the token. |

---

## Task 1: The pure `PromptTokens` helper

**Files:**
- Create: `src/Diariz.Api/Services/PromptTokens.cs`
- Test: `tests/Diariz.Api.Tests/PromptTokensTests.cs`

**Interfaces:**
- Consumes: `TemplateContent`, `TemplateSection`, `TemplateBlock` from `src/Diariz.Api/Services/TemplateContent.cs`. All three are `record`s, so `with` expressions work.
- Produces:
  - `static string PromptTokens.Substitute(string? text, string? userName)` - returns `text ?? ""` unchanged when `userName` is null or blank.
  - `static TemplateContent PromptTokens.Apply(TemplateContent content, string? userName)` - a new content with every section `Title` and every block `Text` substituted. Returns the same instance when there is nothing to do.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/PromptTokensTests.cs`:

```csharp
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The $USERNAME token: a prompt-time substitution, deliberately separate from the {{field}}
/// mechanism, which is output-only and never enters a prompt (see TemplateFields).</summary>
public class PromptTokensTests
{
    [Fact]
    public void Substitute_ReplacesTheToken()
    {
        Assert.Equal(
            "What role did Ada Lovelace play?",
            PromptTokens.Substitute("What role did $USERNAME play?", "Ada Lovelace"));
    }

    [Fact]
    public void Substitute_ReplacesEveryOccurrence()
    {
        Assert.Equal(
            "Ken said. Ken listened.",
            PromptTokens.Substitute("$USERNAME said. $USERNAME listened.", "Ken"));
    }

    /// <summary>The word boundary is the whole reason this is a regex and not a string replace: a literal
    /// $USERNAMES in a prompt must survive rather than becoming "KenS".</summary>
    [Fact]
    public void Substitute_LeavesALongerTokenAlone()
    {
        Assert.Equal("$USERNAMES and $USERNAME_ID", PromptTokens.Substitute("$USERNAMES and $USERNAME_ID", "Ken"));
    }

    [Fact]
    public void Substitute_IsCaseSensitive()
    {
        Assert.Equal("$username", PromptTokens.Substitute("$username", "Ken"));
    }

    /// <summary>Leaving the token in place reads as an obvious fault. Substituting an empty string would
    /// produce "What role did  play?", which reads as a model failure instead.</summary>
    [Fact]
    public void Substitute_LeavesTheTokenWhenThereIsNoName()
    {
        Assert.Equal("Ask $USERNAME", PromptTokens.Substitute("Ask $USERNAME", null));
        Assert.Equal("Ask $USERNAME", PromptTokens.Substitute("Ask $USERNAME", "   "));
    }

    [Fact]
    public void Substitute_LeavesUnrelatedDollarTextAlone()
    {
        Assert.Equal("Costs $500 and $USER stuff", PromptTokens.Substitute("Costs $500 and $USER stuff", "Ken"));
    }

    [Fact]
    public void Substitute_HandlesNullText()
    {
        Assert.Equal("", PromptTokens.Substitute(null, "Ken"));
    }

    /// <summary>Prompt blocks AND literal blocks AND section titles, in one pass - so the token means the
    /// same thing wherever it appears in a formula.</summary>
    [Fact]
    public void Apply_SubstitutesTitlesPromptsAndBoilerplate()
    {
        var content = new TemplateContent([
            new TemplateSection(1, "Notes for $USERNAME", [
                new TemplateBlock(TemplateBlock.Prompt, Text: "What did $USERNAME decide?"),
                new TemplateBlock(TemplateBlock.Boilerplate, Text: "Prepared for $USERNAME."),
                new TemplateBlock(TemplateBlock.FieldKind, Field: "date"),
                new TemplateBlock(TemplateBlock.HorizontalLine),
            ]),
        ]);

        var applied = PromptTokens.Apply(content, "Ada Lovelace");

        Assert.Equal("Notes for Ada Lovelace", applied.Sections[0].Title);
        Assert.Equal("What did Ada Lovelace decide?", applied.Sections[0].Blocks[0].Text);
        Assert.Equal("Prepared for Ada Lovelace.", applied.Sections[0].Blocks[1].Text);
        Assert.Equal("date", applied.Sections[0].Blocks[2].Field); // untouched
        Assert.Null(applied.Sections[0].Blocks[3].Text);           // hr carries no text
    }

    [Fact]
    public void Apply_ReturnsTheSameContent_WhenThereIsNoName()
    {
        var content = TemplateContent.FromPrompt("Ask $USERNAME");

        Assert.Same(content, PromptTokens.Apply(content, null));
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PromptTokensTests"
```

Expected: compile error - `PromptTokens` does not exist.

- [ ] **Step 3: Write the helper**

Create `src/Diariz.Api/Services/PromptTokens.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Diariz.Api.Services;

/// <summary>Prompt-time token substitution for a formula's template. Today there is exactly one token,
/// <c>$USERNAME</c>: the name the running user appears under in their own transcripts.
///
/// <para><b>Why this is not a <c>{{field}}</c>.</b> The template system already has placeholders, resolved by
/// <see cref="TemplateFields"/> - but those are deliberately OUTPUT-only: a field is stamped into the produced
/// document and never enters a prompt, which is what makes <c>{{transcript}}</c> cost no tokens. This token has
/// to reach the model, so it cannot be a field. The two conventions coexist on purpose.</para>
///
/// <para>Pure - no database, no configuration - so the substitution rules are unit-testable on their own and the
/// run pipeline only has to decide WHOSE name to pass.</para></summary>
public static partial class PromptTokens
{
    /// <summary>The word boundary is load-bearing: without it a literal <c>$USERNAMES</c> in someone's prompt
    /// would silently become "Ada LovelaceS". Case-sensitive, so ordinary prose containing "$username" is left
    /// alone.</summary>
    [GeneratedRegex(@"\$USERNAME\b")]
    private static partial Regex UserNameToken();

    /// <summary>Replace <c>$USERNAME</c> in one string. A null or blank name leaves the token in place rather
    /// than deleting it: "What role did $USERNAME play" is an obvious fault, "What role did  play" reads as the
    /// model having failed.</summary>
    public static string Substitute(string? text, string? userName)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (string.IsNullOrWhiteSpace(userName)) return text;
        return UserNameToken().Replace(text, userName.Trim());
    }

    /// <summary>Apply <see cref="Substitute"/> across a whole template - every section title, and every block's
    /// text. Field blocks and horizontal rules carry no text and are copied through untouched. Returns the input
    /// unchanged when there is no name to substitute, so the common path allocates nothing.</summary>
    public static TemplateContent Apply(TemplateContent content, string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return content;

        return content with
        {
            Sections = (content.Sections ?? []).Select(section => section with
            {
                Title = Substitute(section.Title, userName),
                Blocks = (section.Blocks ?? []).Select(block =>
                    block.Text is null ? block : block with { Text = Substitute(block.Text, userName) }).ToList(),
            }).ToList(),
        };
    }
}
```

- [ ] **Step 4: Run the tests and verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PromptTokensTests"
```

Expected: 9 passed.

- [ ] **Step 5: Mutation-check one test**

Tautological tests are the dominant defect class in this repo, so prove one of these can actually fail. Temporarily change the regex to `@"\$NOBODY\b"`, re-run, and confirm `Substitute_ReplacesTheToken` fails. Then revert **by editing the file back in place** - do not restore from a copy, because a restored file keeps its old mtime and MSBuild will skip the rebuild, leaving you testing the mutated binary.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/PromptTokens.cs tests/Diariz.Api.Tests/PromptTokensTests.cs
git commit -m "feat(api): pure \$USERNAME token substitution for formula templates"
```

---

## Task 2: Resolve the running user's transcript name

**Files:**
- Modify: `src/Diariz.Api/Services/FormulaRunProcessor.cs`
- Test: `tests/Diariz.Api.Tests/PromptTokensTests.cs` (or a new `TranscriptNameTests.cs` - either is fine, keep it in one place)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `internal static Task<string?> FormulaRunProcessor.TranscriptNameAsync(DiarizDbContext db, Guid userId, CancellationToken ct)`. Returns the user's linked `Person.Name`, else `ApplicationUser.FullName`, else `Email`, else null.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/TranscriptNameTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Whose name $USERNAME resolves to: the person the account IS in the directory, because that is the
/// name the transcript actually shows. The person's name is kept equal to the display name by
/// PeopleDirectory.SyncFromUserAsync, so the fallbacks are for accounts that predate the directory.</summary>
public class TranscriptNameTests
{
    [Fact]
    public async Task PrefersTheLinkedPersonsName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Display Name" });
        db.People.Add(new Person { Id = Guid.NewGuid(), LinkedUserId = userId, Name = "Transcript Name" });
        await db.SaveChangesAsync();

        Assert.Equal("Transcript Name", await FormulaRunProcessor.TranscriptNameAsync(db, userId, default));
    }

    [Fact]
    public async Task FallsBackToTheDisplayName_WhenThereIsNoPerson()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Display Name" });
        await db.SaveChangesAsync();

        Assert.Equal("Display Name", await FormulaRunProcessor.TranscriptNameAsync(db, userId, default));
    }

    [Fact]
    public async Task FallsBackToTheEmail_WhenThereIsNoName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test" });
        await db.SaveChangesAsync();

        Assert.Equal("a@b.test", await FormulaRunProcessor.TranscriptNameAsync(db, userId, default));
    }

    /// <summary>A formula run must never write to the people directory as a side effect. This is why the lookup
    /// is a plain query and NOT IPeopleDirectory.EnsureForUserAsync, which mints a person.</summary>
    [Fact]
    public async Task DoesNotProvisionAPerson()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Display Name" });
        await db.SaveChangesAsync();

        await FormulaRunProcessor.TranscriptNameAsync(db, userId, default);

        Assert.Empty(db.People);
    }

    [Fact]
    public async Task ReturnsNull_ForAnUnknownUser()
    {
        using var db = TestDb.Create();

        Assert.Null(await FormulaRunProcessor.TranscriptNameAsync(db, Guid.NewGuid(), default));
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TranscriptNameTests"
```

Expected: compile error - no `TranscriptNameAsync`.

- [ ] **Step 3: Implement the lookup**

In `src/Diariz.Api/Services/FormulaRunProcessor.cs`, beside the existing private `OwnerEmailAsync` helper:

```csharp
    /// <summary>The name $USERNAME resolves to: the name this account appears under in transcripts.
    ///
    /// That is the linked <see cref="Person"/>'s name (<c>Person.LinkedUserId</c>), which
    /// <c>PeopleDirectory.SyncFromUserAsync</c> keeps equal to the display name - so the fallbacks only matter
    /// for an account that predates the directory or was provisioned by a path that skipped it.
    ///
    /// Deliberately a plain query and NOT <c>IPeopleDirectory.EnsureForUserAsync</c>: that mints a person as a
    /// side effect, and running a formula is not a reason to write to the directory.</summary>
    internal static async Task<string?> TranscriptNameAsync(DiarizDbContext db, Guid userId, CancellationToken ct)
    {
        var personName = await db.People
            .Where(p => p.LinkedUserId == userId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(personName)) return personName;

        return await db.Users
            .Where(u => u.Id == userId)
            .Select(u => string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName)
            .FirstOrDefaultAsync(ct);
    }
```

- [ ] **Step 4: Run the tests and verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TranscriptNameTests"
```

Expected: 5 passed.

If `FallsBackToTheEmail_WhenThereIsNoName` fails because the in-memory provider cannot translate the conditional projection, replace the second query with a two-step read (`Select(u => new { u.FullName, u.Email })`, then pick in memory). Do not weaken the assertion.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/FormulaRunProcessor.cs tests/Diariz.Api.Tests/TranscriptNameTests.cs
git commit -m "feat(api): resolve the running user's transcript name for prompt tokens"
```

---

## Task 3: Wire the substitution into both run paths

**Files:**
- Modify: `src/Diariz.Api/Services/FormulaRunProcessor.cs`
- Modify: `src/Diariz.Api/Services/FormulaRunner.cs`
- Test: `tests/Diariz.Api.Tests/FormulaRunnerTests.cs`
- Test: `tests/Diariz.Api.Tests/FormulaRunProcessorTests.cs`

**Interfaces:**
- Consumes: `PromptTokens.Apply(TemplateContent, string?)` (Task 1) and `FormulaRunProcessor.TranscriptNameAsync(DiarizDbContext, Guid, CancellationToken)` (Task 2).
- Produces: both run helpers gain a `string? userName` parameter **before** `ct`:
  - `internal static Task<string> RunOverRecordingAsync(DiarizDbContext db, IChatStreamClient chat, LlmRequestConfig cfg, Formula formula, Guid recordingId, string? userName, CancellationToken ct)`
  - `internal static Task<string> RunOverSectionAsync(DiarizDbContext db, IChatStreamClient chat, LlmRequestConfig cfg, Formula formula, Guid sectionId, string? userName, CancellationToken ct)`

  There are exactly three call sites: `FormulaRunner.cs:116`, `FormulaRunProcessor.cs:61`, `FormulaRunProcessor.cs:63`.

- [ ] **Step 1: Write the failing test for the synchronous path**

Append to `tests/Diariz.Api.Tests/FormulaRunnerTests.cs`. Read the file's `SeedRecordingWithTranscript` helper first - reuse it rather than duplicating.

```csharp
    /// <summary>$USERNAME reaches the model already substituted. Asserted on the SYSTEM MESSAGE the fake chat
    /// client received, not on PromptTokens - the helper's own tests cannot prove it is wired in.</summary>
    [Fact]
    public async Task RunAsync_SubstitutesTheUserNameIntoThePrompt()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Ignored" });
        db.People.Add(new Person { Id = Guid.NewGuid(), LinkedUserId = userId, Name = "Ada Lovelace" });

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId, Name = "Role",
            ContentJson = TemplateContent.FromPrompt("What role did $USERNAME play in this meeting?").Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var chat = new FakeChatStreamClient();
        await MakeRunner(db, chat, new FakeLlmSettingsResolver()).RunAsync(userId, rec.Id, formula.Id);

        Assert.Equal("What role did Ada Lovelace play in this meeting?", chat.LastMessages![0].Content);
    }

    /// <summary>Literal text is substituted too, so a token in boilerplate does not survive into the produced
    /// document looking like a bug.</summary>
    [Fact]
    public async Task RunAsync_SubstitutesTheUserNameIntoBoilerplate()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Ada Lovelace" });

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
```

- [ ] **Step 2: Run the tests and verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SubstitutesTheUserName"
```

Expected: FAIL with the assertion showing the literal `$USERNAME` still present. Quote that output in your commit or PR notes - this is the red that proves the test bites.

- [ ] **Step 3: Add the parameter and apply the substitution in `RunOverRecordingAsync`**

```csharp
    internal static async Task<string> RunOverRecordingAsync(
        DiarizDbContext db, IChatStreamClient chat, LlmRequestConfig cfg,
        Formula formula, Guid recordingId, string? userName, CancellationToken ct)
    {
        // Substituted on the PARSED template, before composing, so prompt blocks and literal blocks agree
        // without either path knowing about tokens.
        var content = PromptTokens.Apply(TemplateContent.Parse(formula.ContentJson), userName);
        var context = await BuildRecordingContextAsync(db, recordingId, formula.Context, ct: ct);
        var fields = await BuildFieldResolverAsync(db, content, recordingId, ct);
        return await ComposeAsync(chat, cfg, content, fields, context, ct);
    }
```

- [ ] **Step 4: Do the same in `RunOverSectionAsync`**

Change the signature to add `string? userName` before `ct`, and change its `TemplateContent.Parse` line to:

```csharp
        var content = PromptTokens.Apply(TemplateContent.Parse(formula.ContentJson), userName);
```

The map loop and the reduce both read this one `content`, so both get the substitution.

- [ ] **Step 5: Update the synchronous caller**

In `src/Diariz.Api/Services/FormulaRunner.cs`, in `RunAsync`, before the run call:

```csharp
        var userName = await FormulaRunProcessor.TranscriptNameAsync(_db, userId, ct);
```

and pass it:

```csharp
        var text = await FormulaRunProcessor.RunOverRecordingAsync(_db, _chat, cfg, formula, recordingId, userName, ct);
```

- [ ] **Step 6: Update the async job caller**

In `FormulaRunProcessor`'s job handler, before the `try` block:

```csharp
        // Whose name $USERNAME resolves to: the job's user. For an automatic, meeting-type-triggered run that
        // is the recording's owner, which is the right answer - nobody else asked for it.
        var userName = await TranscriptNameAsync(db, job.UserId, ct);
```

and pass `userName` into both `RunOverRecordingAsync` and `RunOverSectionAsync`.

- [ ] **Step 7: Write the failing test for the async path**

Append to `tests/Diariz.Api.Tests/FormulaRunProcessorTests.cs`. It reuses that file's existing private helpers `SeedRecordingWithTranscript(db, userId)`, `SeedFormulaAndResult(db, userId, recordingId)` and `Run(db, chat, resolver, hub, job)`; the job record is `FormulaRunJob(Guid? RecordingId, Guid? SectionId, Guid ResultId, Guid FormulaId, Guid UserId)`.

The point of this test, distinct from Task 3 Step 1, is that the **async** path resolves the name from `job.UserId` rather than from an ambient current user.

```csharp
    /// <summary>The async path has no "current user" - it must take the name from job.UserId, which for an
    /// automatic meeting-type-triggered run is the recording's owner.</summary>
    [Fact]
    public async Task ProcessAsync_SubstitutesTheJobUsersName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);

        formula.ContentJson = TemplateContent.FromPrompt("Ask $USERNAME").Serialize();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Ignored" });
        db.People.Add(new Person { Id = Guid.NewGuid(), LinkedUserId = userId, Name = "Ada Lovelace" });
        await db.SaveChangesAsync();

        var chat = new FakeChatStreamClient();
        await Run(db, chat, new FakeLlmSettingsResolver(), new FakeHubContext(),
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId));

        Assert.Equal("Ask Ada Lovelace", chat.LastMessages![0].Content);
    }
```

If `SeedRecordingWithTranscript` already adds an `ApplicationUser` row for `userId`, drop the `db.Users.Add(...)` line rather than adding a duplicate - read the helper before writing this.

- [ ] **Step 8: Build the solution and run the tests**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
```

Expected: build succeeded with 0 warnings; all tests pass. Build `Diariz.slnx` (not just the unit project) so a broken construction site in the integration project surfaces now rather than in CI.

- [ ] **Step 9: Commit**

```bash
git add src/Diariz.Api/Services/FormulaRunProcessor.cs src/Diariz.Api/Services/FormulaRunner.cs tests/Diariz.Api.Tests/FormulaRunnerTests.cs tests/Diariz.Api.Tests/FormulaRunProcessorTests.cs
git commit -m "feat(api): substitute \$USERNAME when a formula runs"
```

---

## Task 4: Document the token

**Files:**
- Modify: `apps/web/src/content/help/en/formula-configuration.md`

There is no editor UI for this by design, so the help article is the only place a user can discover it.

- [ ] **Step 1: Add the section**

In `apps/web/src/content/help/en/formula-configuration.md`, add a section near the merge-fields section (read the file to find where that is and match its heading level and tone):

```markdown
## Your own name in a prompt

Write `$USERNAME` anywhere in a formula - in a prompt block or in ordinary text - and it is replaced, each
time the formula runs, with the name you appear under on your transcripts. That lets a formula ask about you
specifically:

- "What role did $USERNAME play in this meeting?"
- "What was the attitude of speakers apart from $USERNAME?"

The name comes from your entry in the people directory, which follows the display name on your profile. When a
formula runs automatically, the name used is the owner of the recording.

This is not the same thing as a merge field. Merge fields like `{{date}}` are written into the finished
document and the model never sees them; `$USERNAME` is substituted before the model is asked, which is what
makes it usable inside a question.
```

ASCII only, plain hyphens, no em dashes.

- [ ] **Step 2: Run the help content gate**

```bash
cd apps/web && npx vitest run src/content/help/helpContent.test.ts
```

Expected: PASS. It checks the front-matter block, ASCII-only content, and that every `<HelpButton topic="...">` points at an article that exists.

- [ ] **Step 3: Commit**

```bash
git add apps/web/src/content/help/en/formula-configuration.md
git commit -m "docs(help): document the \$USERNAME token in formulas"
```

---

## Task 5: Verify it live

**Files:** none.

- [ ] **Step 1: Confirm which stack you are pointing at**

The local `diariz` compose stack **is production** on this machine.

```bash
docker exec diariz-api-1 printenv App__PublicUrl
```

Prefer the dev stack for this. If you must use the local one, only run a formula against your own recording - a run writes a `FormulaResult` row.

- [ ] **Step 2: Rebuild the API**

```bash
cd deploy && docker compose up -d --build api
```

A stale build is the most common cause of "it did not work" here. If live and test disagree, believe the test and force a fresh build.

- [ ] **Step 3: Run a formula containing the token**

Create a personal formula whose prompt is `What role did $USERNAME play in this meeting?`, run it against a recording where you speak, and confirm the answer is about you by name rather than about a literal "$USERNAME".

- [ ] **Step 4: Confirm the model actually received the substituted text**

```bash
docker exec diariz-postgres-1 psql -U diariz -d diariz -c "select \"Kind\", \"Model\", left(\"RequestPreview\", 200) from \"LlmCalls\" order by \"CreatedAt\" desc limit 3;"
```

Adjust the column name if `RequestPreview` is not what the table calls it - read the schema first with `\d "LlmCalls"`. Note that `LlmCallKind.FormulaRun` and `LlmCallGroup` are different numberings; filter on `Kind` only after checking which enum the column stores.

---

## Task 6: Release paperwork

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/web/package-lock.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`
- Modify: `docs/Overall_Synopsis_of_Platform.md`

- [ ] **Step 1: Bump the version everywhere**

Read `version.json`, apply Minor +1 / Build 0, and write the same value into all five mirrors. `apps/web/package-lock.json` holds it in **two** places (the root `version` and the `packages[""]` entry). `apps/web/src/lib/versionMirrors.test.ts` fails the build on drift.

- [ ] **Step 2: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`: `version` (equal to `version.json`), `date`, `pr`, `headline`, a prose `summary` explaining what the token does and that it is substituted before the model sees the prompt, and an `added` bullet. `releases.test.ts` asserts `RELEASES[0].version === version.json`. Plain hyphens only.

The `pr` number must be written **before** `gh pr create` exists to report it. Do not guess "last + 1" - check the repository's current issue/PR sequence, since issues and Dependabot PRs share it.

- [ ] **Step 3: Update the inventories**

- README **Features** table: a row for the token.
- `docs/features.md`: the matching prose bullet. Always both, never one.
- `CAPABILITIES` table in `releases.ts`: add or amend the formulas row.

- [ ] **Step 4: Update the synopsis**

`docs/Overall_Synopsis_of_Platform.md`: note that a formula's template is token-substituted before composition, and that `$USERNAME` resolves to the run user's linked person. No schema change, so `docs/Data_Schema.md` is untouched.

- [ ] **Step 5: Run everything**

```bash
dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests
cd apps/web && npm run build && npm test
```

Expected: green throughout, no warnings.

- [ ] **Step 6: Commit, push and open the PR**

```bash
git add version.json apps/web/package.json apps/web/package-lock.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md docs/Overall_Synopsis_of_Platform.md
git commit -m "chore: release <version>"
git push -u origin <branch>
gh pr create --title "..." --body "..."
```

The PR body must state the deployment surface: **server redeploy only, no desktop release**. No issue to close.
