# Formulas Phase B - File-based built-in formulas (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this
> plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Move the four Diariz-provided (built-in) formulas out of hard-coded C# literals into git-editable
markdown files (`src/Diariz.Api/formulas/*.md`) with a small frontmatter, loaded at boot - mirroring the
existing editable `prompts/*.md` pattern. Behaviour is preserved (same 4 formulas, same create-only-by-name
seeding); the payoff is that the shipped set can be edited or extended without a code change.

**Architecture:** API only (`src/Diariz.Api`). A **pure parser** (`BuiltInFormulaCatalog.Parse`, unit-tested,
no I/O) turns a markdown+frontmatter file into a `BuiltInFormulaSpec`; `BuiltInFormulaCatalog.LoadFrom(dir)`
does the file I/O (skips a bad file rather than crashing boot). `Seeder.SeedFormulasAsync` now takes the
loaded specs instead of literals. `Program.cs` resolves the `formulas/` directory exactly like it already
resolves `prompts/` and `locales/`.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core; xUnit unit tests (in-memory `TestDb`); markdown files as
`CopyToOutputDirectory` content.

**Release:** Build bump `0.130.1` -> `0.130.2` (internal refactor; no user-facing behaviour change).
**Deployment:** server redeploy (API). No migration. No desktop release.

---

## Existing pattern this mirrors (read first)
- `src/Diariz.Api/Diariz.Api.csproj:57` - `<Content Include="prompts\**\*.md" CopyToOutputDirectory="PreserveNewest" />`.
- `src/Diariz.Api/Program.cs:274-279` - resolves `promptsDir` as `ContentRootPath/prompts` if it exists, else
  `AppContext.BaseDirectory/prompts`.
- `src/Diariz.Api/Services/PromptTemplateProvider.cs` - `FilePromptTemplateProvider` reads files, swallows
  `IOException`, falls back gracefully.
- Current seeding: `src/Diariz.Api/Services/Seeder.cs:126-203` (`SeedFormulasAsync(DiarizDbContext db)` with
  4 hard-coded literals + a create-only-by-name `EnsureFormula`), called at `Program.cs:462`.
- Current tests: `tests/Diariz.Api.Tests/SeederFormulasTests.cs` (3 tests calling `SeedFormulasAsync(db)`).

## Files
- Create: `src/Diariz.Api/Services/BuiltInFormulaCatalog.cs` (record `BuiltInFormulaSpec` + `Parse` + `LoadFrom`).
- Create: `src/Diariz.Api/formulas/follow-up-email.md`, `meeting-recap.md`, `decisions-and-risks.md`,
  `tone-and-sentiment-read.md`.
- Modify: `src/Diariz.Api/Diariz.Api.csproj` (add the `formulas\**\*.md` Content include; bump `<Version>`).
- Modify: `src/Diariz.Api/Services/Seeder.cs` (`SeedFormulasAsync` takes `IReadOnlyList<BuiltInFormulaSpec>`).
- Modify: `src/Diariz.Api/Program.cs` (resolve `formulasDir`; pass loaded specs to the seeder).
- Modify: `tests/Diariz.Api.Tests/SeederFormulasTests.cs` (seed from specs; keep the same assertions).
- Modify: `tests/Diariz.Api.Tests/Diariz.Api.Tests.csproj` (link the shipped `formulas/*.md` into test output).
- Create: `tests/Diariz.Api.Tests/BuiltInFormulaCatalogTests.cs` (parser + loader tests).
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json` (mirrors), `apps/web/src/lib/releases.ts`.
- Modify: `docs/Overall_Synopsis_of_Platform.md` (the SeedFormulasAsync sentence, ~L369).

---

## Task B1: The pure frontmatter parser (TDD)

**Files:**
- Create: `tests/Diariz.Api.Tests/BuiltInFormulaCatalogTests.cs`
- Create: `src/Diariz.Api/Services/BuiltInFormulaCatalog.cs`

- [ ] **Step 1: Write the failing parser tests**

Create `tests/Diariz.Api.Tests/BuiltInFormulaCatalogTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The built-in-formula markdown loader: a pure frontmatter parser (no I/O) plus a resilient
/// directory loader. The shipped formulas/ folder must parse to the four Diariz-provided formulas.</summary>
public class BuiltInFormulaCatalogTests
{
    private const string Valid =
        "---\n" +
        "name: Follow-up email\n" +
        "description: Draft a follow-up email.\n" +
        "context: Transcript, Summary, Actions\n" +
        "---\n" +
        "Write a concise follow-up email.\n\nSecond paragraph.";

    [Fact]
    public void Parse_reads_all_fields()
    {
        var spec = BuiltInFormulaCatalog.Parse(Valid, "follow-up-email.md");
        Assert.Equal("Follow-up email", spec.Name);
        Assert.Equal("Draft a follow-up email.", spec.Description);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Actions, spec.Context);
        Assert.Equal("Write a concise follow-up email.\n\nSecond paragraph.", spec.Prompt);
    }

    [Fact]
    public void Parse_handles_crlf_and_bom()
    {
        var withCrlf = "﻿" + Valid.Replace("\n", "\r\n");
        var spec = BuiltInFormulaCatalog.Parse(withCrlf, "x.md");
        Assert.Equal("Follow-up email", spec.Name);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Actions, spec.Context);
    }

    [Fact]
    public void Parse_missing_context_defaults_to_none()
    {
        var text = "---\nname: X\n---\nBody.";
        var spec = BuiltInFormulaCatalog.Parse(text, "x.md");
        Assert.Equal(FormulaContext.None, spec.Context);
        Assert.Null(spec.Description);
    }

    [Theory]
    [InlineData("no frontmatter at all")]                       // missing opening ---
    [InlineData("---\nname: X\nBody without closing fence")]    // unterminated frontmatter
    [InlineData("---\ndescription: no name\n---\nBody.")]       // missing required name
    [InlineData("---\nname: X\ncontext: Bogus\n---\nBody.")]    // invalid context flag
    [InlineData("---\nname: X\n---\n   \n")]                    // empty body
    public void Parse_rejects_malformed(string text)
    {
        Assert.Throws<FormatException>(() => BuiltInFormulaCatalog.Parse(text, "bad.md"));
    }

    [Fact]
    public void LoadFrom_missing_directory_returns_empty()
    {
        Assert.Empty(BuiltInFormulaCatalog.LoadFrom(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid())));
    }

    [Fact]
    public void LoadFrom_skips_a_malformed_file_but_keeps_the_good_ones()
    {
        var dir = Path.Combine(Path.GetTempPath(), "formulas-" + Guid.NewGuid("N".ToString()));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "good.md"), Valid);
            File.WriteAllText(Path.Combine(dir, "bad.md"), "not a formula");
            var specs = BuiltInFormulaCatalog.LoadFrom(dir);
            Assert.Single(specs);
            Assert.Equal("Follow-up email", specs[0].Name);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The real shipped folder (copied into the test output via the csproj content link) must parse
    /// to exactly the four Diariz-provided formulas with their expected context masks - behaviour-preserving
    /// vs the old C# literals.</summary>
    [Fact]
    public void Shipped_folder_parses_to_the_four_builtins()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "formulas");
        var specs = BuiltInFormulaCatalog.LoadFrom(dir);
        Assert.Equal(4, specs.Count);
        Assert.All(specs, s => Assert.False(string.IsNullOrWhiteSpace(s.Prompt)));

        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Actions,
            specs.Single(s => s.Name == "Follow-up email").Context);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary,
            specs.Single(s => s.Name == "Meeting recap").Context);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Minutes | FormulaContext.Actions,
            specs.Single(s => s.Name == "Decisions & risks").Context);
        Assert.Equal(FormulaContext.Transcript,
            specs.Single(s => s.Name == "Tone & sentiment read").Context);
    }
}
```

> Note: fix the obvious typo when you paste - `Guid.NewGuid("N".ToString())` should be `Guid.NewGuid():N` in an
> interpolated string, i.e. use `"formulas-" + Guid.NewGuid().ToString("N")`. (Included here so you don't
> mimic a bad token.)

- [ ] **Step 2: Run - verify it fails to compile (red)**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~BuiltInFormulaCatalog"`
Expected: FAIL - `BuiltInFormulaCatalog` does not exist.

- [ ] **Step 3: Implement the parser + loader**

Create `src/Diariz.Api/Services/BuiltInFormulaCatalog.cs`:

```csharp
using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>A Diariz-provided (built-in) formula loaded from a git-editable markdown file in formulas/.</summary>
public record BuiltInFormulaSpec(string Name, string? Description, string Prompt, FormulaContext Context);

/// <summary>Loads the Diariz-provided starter formulas from formulas/*.md, mirroring the editable prompts/
/// templates. Each file is a small key:value frontmatter block delimited by '---' lines, then the prompt as
/// the markdown body:
/// <code>
/// ---
/// name: Follow-up email
/// description: Draft a follow-up email.
/// context: Transcript, Summary, Actions
/// ---
/// (prompt body...)
/// </code>
/// <see cref="Parse"/> is pure (unit-tested); <see cref="LoadFrom"/> does the I/O and skips a
/// malformed/unreadable file rather than crashing boot.</summary>
public static class BuiltInFormulaCatalog
{
    // Transcript|Notes|Attachments|Summary|Minutes|Actions = 63; rejects unknown bits from a numeric context.
    private const int ValidContextMask =
        (int)(FormulaContext.Transcript | FormulaContext.Notes | FormulaContext.Attachments
            | FormulaContext.Summary | FormulaContext.Minutes | FormulaContext.Actions);

    /// <summary>Parse one formula markdown file. Throws <see cref="FormatException"/> on malformed input
    /// (missing/unterminated frontmatter, missing name, empty body, or an unknown context flag).</summary>
    public static BuiltInFormulaSpec Parse(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n").TrimStart('﻿').TrimStart();
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            throw new FormatException($"{source}: must start with a '---' frontmatter block.");

        var afterOpen = normalized[4..]; // past "---\n"
        var endIdx = afterOpen.IndexOf("\n---", StringComparison.Ordinal);
        if (endIdx < 0)
            throw new FormatException($"{source}: frontmatter block is not closed with '---'.");

        var frontmatter = afterOpen[..endIdx];
        var body = afterOpen[(endIdx + 4)..].TrimStart('\n').Trim();
        if (body.Length == 0)
            throw new FormatException($"{source}: the prompt body (after the frontmatter) is empty.");

        string? name = null, description = null, contextRaw = null;
        foreach (var raw in frontmatter.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) throw new FormatException($"{source}: frontmatter line '{line}' is not 'key: value'.");
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "name": name = value; break;
                case "description": description = value; break;
                case "context": contextRaw = value; break;
                // Unknown keys ignored for forward-compatibility.
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            throw new FormatException($"{source}: missing required 'name'.");

        var context = FormulaContext.None;
        if (!string.IsNullOrWhiteSpace(contextRaw))
        {
            if (!Enum.TryParse(contextRaw, ignoreCase: true, out context)
                || ((int)context & ~ValidContextMask) != 0)
                throw new FormatException(
                    $"{source}: invalid context '{contextRaw}'. Use a comma-separated list of "
                    + "Transcript, Notes, Attachments, Summary, Minutes, Actions.");
        }

        return new BuiltInFormulaSpec(
            name.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            body,
            context);
    }

    /// <summary>Read and parse every formulas/*.md in <paramref name="dir"/> (filename order). A missing
    /// directory yields an empty list; a malformed/unreadable file is skipped (logged) so one bad file can't
    /// crash boot.</summary>
    public static IReadOnlyList<BuiltInFormulaSpec> LoadFrom(string dir, ILogger? log = null)
    {
        if (!Directory.Exists(dir))
        {
            log?.LogWarning("Built-in formulas directory not found: {Dir}", dir);
            return [];
        }

        var specs = new List<BuiltInFormulaSpec>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.md")
                     .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            try { specs.Add(Parse(File.ReadAllText(path), Path.GetFileName(path))); }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                log?.LogWarning("Skipping built-in formula {File}: {Error}", Path.GetFileName(path), ex.Message);
            }
        }
        return specs;
    }
}
```

- [ ] **Step 4: Add the csproj content link for the test (needed for the shipped-folder test)**

In `tests/Diariz.Api.Tests/Diariz.Api.Tests.csproj`, add an `ItemGroup` so the real files land in the test
output dir (`AppContext.BaseDirectory/formulas`):

```xml
  <ItemGroup>
    <Content Include="..\..\src\Diariz.Api\formulas\*.md" Link="formulas\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

(The `.md` files themselves are created in Task B2 - the `Shipped_folder_...` test will stay red until then;
the other parser/loader tests should pass now.)

- [ ] **Step 5: Run the parser/loader tests (shipped-folder one still red - files land in B2)**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~BuiltInFormulaCatalog"`
Expected: all PASS **except** `Shipped_folder_parses_to_the_four_builtins` (no files yet). If any OTHER test
fails, fix the parser.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/BuiltInFormulaCatalog.cs tests/Diariz.Api.Tests/BuiltInFormulaCatalogTests.cs tests/Diariz.Api.Tests/Diariz.Api.Tests.csproj
git commit -m "feat(formulas): pure markdown+frontmatter parser and directory loader for built-in formulas"
```

---

## Task B2: The four built-in formula files + csproj content include

**Files:**
- Create: `src/Diariz.Api/formulas/follow-up-email.md`, `meeting-recap.md`, `decisions-and-risks.md`,
  `tone-and-sentiment-read.md`
- Modify: `src/Diariz.Api/Diariz.Api.csproj`

Reproduce the current prompt bodies **verbatim** so behaviour is preserved.

- [ ] **Step 1: `src/Diariz.Api/formulas/follow-up-email.md`**

```md
---
name: Follow-up email
description: Draft a follow-up email summarising the meeting and next steps.
context: Transcript, Summary, Actions
---
Write a concise, professional follow-up email in Markdown based on the meeting context provided.

Structure it as:
- A brief greeting
- A 2-4 sentence recap of what the meeting covered
- A bulleted list of the agreed actions, each with its owner
- A short closing line

Keep the tone warm but businesslike, and do not invent actions or owners that are not
supported by the context.
```

- [ ] **Step 2: `src/Diariz.Api/formulas/meeting-recap.md`**

```md
---
name: Meeting recap
description: A short shareable recap of the meeting.
context: Transcript, Summary
---
Write a crisp Markdown recap of the meeting based on the context provided.

Start with a one-line TL;DR, then 3-6 bullet points covering the highlights. Keep each
bullet to a single sentence and favor concrete outcomes over general description.
```

- [ ] **Step 3: `src/Diariz.Api/formulas/decisions-and-risks.md`**

```md
---
name: Decisions & risks
description: Extract the decisions made and the risks or open questions raised.
context: Transcript, Minutes, Actions
---
Read the meeting context provided and produce two Markdown sections:

## Decisions
A bulleted list of the concrete decisions that were made.

## Risks & open questions
A bulleted list of the risks, concerns, or unresolved questions that were raised.

If either section has nothing to report, write "None identified" under that heading
instead of leaving it empty.
```

- [ ] **Step 4: `src/Diariz.Api/formulas/tone-and-sentiment-read.md`**

```md
---
name: Tone & sentiment read
description: A read on the emotional tone and sentiment of the meeting.
context: Transcript
---
Read the transcript provided and assess the overall tone and sentiment of the meeting in
a few short Markdown paragraphs.

Cover the general mood, any notable shifts in tone over the course of the conversation, and
any moments of tension, disagreement, or enthusiasm. Be measured and avoid over-claiming -
note where the tone is ambiguous rather than forcing a conclusion.
```

- [ ] **Step 5: Ship the files - add the Content include to `src/Diariz.Api/Diariz.Api.csproj`**

After the existing `prompts\**\*.md` ItemGroup (around L56-58), add:

```xml
  <!-- Editable Diariz-provided formulas, read at boot (edit/mount/extend without a rebuild). -->
  <ItemGroup>
    <Content Include="formulas\**\*.md" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 6: The shipped-folder test now passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~BuiltInFormulaCatalog"`
Expected: ALL pass, including `Shipped_folder_parses_to_the_four_builtins` (the content link copied the 4
files into the test output).

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/formulas src/Diariz.Api/Diariz.Api.csproj
git commit -m "feat(formulas): ship the four built-in formulas as git-editable markdown files"
```

---

## Task B3: Seed from the loaded specs (Seeder + Program wiring + tests)

**Files:**
- Modify: `src/Diariz.Api/Services/Seeder.cs`
- Modify: `src/Diariz.Api/Program.cs`
- Modify: `tests/Diariz.Api.Tests/SeederFormulasTests.cs`

- [ ] **Step 1: Rewrite the seeder-tests to seed from specs (red)**

Replace the body of `tests/Diariz.Api.Tests/SeederFormulasTests.cs` with:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>Diariz-provided starter formulas are seeded once, create-only, and never overwritten on
/// subsequent boots. The specs come from the shipped formulas/*.md (loaded via BuiltInFormulaCatalog).</summary>
public class SeederFormulasTests
{
    private static IReadOnlyList<BuiltInFormulaSpec> Shipped() =>
        BuiltInFormulaCatalog.LoadFrom(Path.Combine(AppContext.BaseDirectory, "formulas"));

    private static readonly IReadOnlyList<BuiltInFormulaSpec> TwoSpecs = new[]
    {
        new BuiltInFormulaSpec("Alpha", "first", "Prompt A", FormulaContext.Transcript),
        new BuiltInFormulaSpec("Beta", null, "Prompt B", FormulaContext.Transcript | FormulaContext.Summary),
    };

    [Fact]
    public async Task SeedFormulasAsync_creates_the_four_builtin_formulas()
    {
        using var db = TestDb.Create();

        await Seeder.SeedFormulasAsync(db, Shipped());

        var formulas = await db.Formulas.ToListAsync();
        Assert.Equal(4, formulas.Count);
        Assert.All(formulas, f =>
        {
            Assert.Equal(FormulaScope.Diariz, f.Scope);
            Assert.True(f.IsBuiltIn);
            Assert.True(f.Enabled);
            Assert.Null(f.OwnerUserId);
        });

        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Actions,
            formulas.Single(f => f.Name == "Follow-up email").Context);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary,
            formulas.Single(f => f.Name == "Meeting recap").Context);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Minutes | FormulaContext.Actions,
            formulas.Single(f => f.Name == "Decisions & risks").Context);
        Assert.Equal(FormulaContext.Transcript,
            formulas.Single(f => f.Name == "Tone & sentiment read").Context);
    }

    [Fact]
    public async Task SeedFormulasAsync_run_twice_does_not_duplicate()
    {
        using var db = TestDb.Create();

        await Seeder.SeedFormulasAsync(db, TwoSpecs);
        await Seeder.SeedFormulasAsync(db, TwoSpecs);

        Assert.Equal(2, await db.Formulas.CountAsync());
    }

    [Fact]
    public async Task SeedFormulasAsync_preserves_an_edited_prompt_on_the_next_run()
    {
        using var db = TestDb.Create();
        await Seeder.SeedFormulasAsync(db, TwoSpecs);

        var formula = await db.Formulas.SingleAsync(f => f.Name == "Beta");
        formula.Prompt = "Custom admin-edited prompt.";
        await db.SaveChangesAsync();

        await Seeder.SeedFormulasAsync(db, TwoSpecs);

        var reloaded = await db.Formulas.SingleAsync(f => f.Name == "Beta");
        Assert.Equal("Custom admin-edited prompt.", reloaded.Prompt);
    }
}
```

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SeederFormulas"`
Expected: FAIL to compile - `SeedFormulasAsync` still takes only `(db)`.

- [ ] **Step 2: Change `SeedFormulasAsync` to take the specs**

In `src/Diariz.Api/Services/Seeder.cs`, replace the whole `SeedFormulasAsync` method (L122-203, the doc
comment through the closing brace) with:

```csharp
    /// <summary>Seed the Diariz-provided starter formulas from the loaded built-in specs (formulas/*.md).
    /// Create-only: if a formula with the same Name already exists (Name is the stable identity for seeds,
    /// there is no key column) it is left untouched, so an admin's edit to a built-in's prompt survives a
    /// reboot - mirroring the EnsureGroup pattern in <see cref="SeedGroupsAsync"/>.</summary>
    public static async Task SeedFormulasAsync(DiarizDbContext db, IReadOnlyList<BuiltInFormulaSpec> builtins)
    {
        foreach (var spec in builtins)
        {
            if (await db.Formulas.AnyAsync(f => f.Name == spec.Name)) continue; // never overwrite an admin edit

            db.Formulas.Add(new Formula
            {
                Id = Guid.NewGuid(),
                Scope = FormulaScope.Diariz,
                OwnerUserId = null,
                Name = spec.Name,
                Description = spec.Description,
                Prompt = spec.Prompt,
                Context = spec.Context,
                Enabled = true,
                IsBuiltIn = true,
            });
        }

        await db.SaveChangesAsync();
    }
```

- [ ] **Step 3: Wire `Program.cs` to load the folder and pass the specs**

In `src/Diariz.Api/Program.cs`, next to the `promptsDir` resolution (after L279), add:

```csharp
// Prefer the content root's formulas/ (dev + published output), else the app base dir. Loaded once at boot.
var formulasDir = Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "formulas"))
    ? Path.Combine(builder.Environment.ContentRootPath, "formulas")
    : Path.Combine(AppContext.BaseDirectory, "formulas");
```

Then replace the seeding call at L462:

```csharp
    await Seeder.SeedFormulasAsync(db);
```
with:
```csharp
    await Seeder.SeedFormulasAsync(db, BuiltInFormulaCatalog.LoadFrom(formulasDir, app.Logger));
```

- [ ] **Step 4: Green + no other call sites broke**

Run: `dotnet build Diariz.slnx` (catches any integration-project construction site per the repo's build note).
Expected: build succeeds.
Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SeederFormulas|FullyQualifiedName~BuiltInFormulaCatalog"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/Seeder.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/SeederFormulasTests.cs
git commit -m "feat(formulas): seed built-in formulas from the loaded markdown specs instead of C# literals"
```

---

## Task B4: Docs + version bump

**Files:**
- Modify: `docs/Overall_Synopsis_of_Platform.md`
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`
- Modify: `apps/web/src/lib/releases.ts`

- [ ] **Step 1: Update the synopsis sentence**

In `docs/Overall_Synopsis_of_Platform.md` (~L369), the sentence currently reads that
`Services/Seeder.SeedFormulasAsync` *"insert-if-missing-by-name seeds four `Diariz`-scope starter formulas"*.
Update it to note they are now loaded from editable files, e.g. append: *"- loaded at boot from git-editable
`src/Diariz.Api/formulas/*.md` (markdown + `name`/`description`/`context` frontmatter, parsed by
`BuiltInFormulaCatalog`; still create-only by name, so admin edits survive), mirroring the editable
`prompts/*.md` templates."* Keep it one concise clause; no em/en dashes.

- [ ] **Step 2: Failing release-invariant guard**

Set `version.json` to `0.130.2` only, then run: `cd apps/web && npm test -- releases`
Expected: FAIL (`RELEASES[0].version` != version.json).

- [ ] **Step 3: Bump the mirrors + prepend the release entry**

Set `<Version>` in `src/Diariz.Api/Diariz.Api.csproj` and `"version"` in `apps/web/package.json` +
`apps/desktop/package.json` to `0.130.2`. Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`:

```ts
  {
    version: "0.130.2",
    date: "2026-07-13",
    pr: 0, // set to the real PR number after opening the PR
    headline: "Built-in formulas are now editable files",
    summary:
      "The four Diariz-provided formulas are no longer baked into the server code - they now live as plain markdown files (formulas/*.md) with a small frontmatter, loaded at startup the same way the prompt templates are. Behaviour is unchanged (same formulas, still create-only so your edits survive restarts), but the shipped set can now be edited or extended without a code change or rebuild.",
    changed: [
      "Built-in formulas load from git-editable markdown files (formulas/*.md) instead of hard-coded server literals; the set can be edited or extended without a rebuild.",
    ],
  },
```

- [ ] **Step 4: Green + full backend build**

Run: `cd apps/web && npm test -- releases` -> PASS.
Run: `dotnet build Diariz.slnx` -> succeeds.

- [ ] **Step 5: Commit**

```bash
git add docs/Overall_Synopsis_of_Platform.md version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts
git commit -m "chore(release): 0.130.2 - built-in formulas as editable files"
```

---

## Finish

- [ ] Full backend unit suite: `dotnet test tests/Diariz.Api.Tests` - green, pristine. (Integration tests
  need Docker; run `dotnet test tests/Diariz.Api.IntegrationTests` if Docker is available - not expected to be
  affected, but the seeder signature changed.)
- [ ] `dotnet build Diariz.slnx` clean.
- [ ] Use **superpowers:finishing-a-development-branch**: push `feat/formulas-phase-b-file-builtins`, open a
  PR. Deployment surface = **server redeploy (API)**; no migration; no desktop release. The new `formulas/*.md`
  ship via the existing publish (identical to `prompts/*.md`).
- [ ] After the PR number is known, set `RELEASES[0].pr`.

## Self-review checklist
- Spec coverage: markdown+frontmatter files (B2) / pure loader no-new-dependency (B1) / seeder reads folder,
  create-only preserved (B3) / CopyToOutputDirectory ships them (B2 csproj) / synopsis + release (B4). ✓
- No placeholders: full code for the parser, loader, seeder, Program wiring, all 4 files, and every test. ✓
- Consistency: `BuiltInFormulaSpec`/`BuiltInFormulaCatalog.Parse`/`LoadFrom` names match across B1/B3 and the
  tests; `formulasDir` mirrors `promptsDir`; version `0.130.2` used in all five spots. ✓
- Behaviour-preserving: the four names + context masks asserted identical to the old literals. ✓
