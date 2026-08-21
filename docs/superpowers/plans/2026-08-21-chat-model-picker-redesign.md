# Chat Model Picker Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Redesign the chat model dropdown into a titled, capability-annotated menu, and add a per-model **Description** an administrator writes and every chat user reads.

**Architecture:** Three layers change. (1) A new nullable `LlmModels.Description` column, surfaced through the administrator-only `LlmModelDto`/`LlmModelUpsert` and edited in the model editor drawer's Connection panel. (2) `ChatModelCatalog` gains `Description` (passed through) and `SupportsTools` (**resolved** through the same `LlmParameterStack` walk that already produces `SupportsImages`, so the picker and the pipeline cannot disagree), both surfaced on `ChatModelDto`. (3) `ChatModelPicker.tsx` rebuilds its menu body: header, capability icons, binary-K context chip, description column, footer legend, 372px width.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core/Postgres, xUnit + Testcontainers, React 19 + TypeScript + Tailwind v4, Vitest + @testing-library/react.

## Global Constraints

- **TDD is mandatory.** Write the failing test, run it, watch it fail with the expected message, then write the minimal code. No production code without a preceding red test.
- **No em/en dashes in user-facing text.** Plain hyphen `-` only, in UI strings, i18n catalogs, release notes, and help articles.
- **Locale keys must be identical across all four folders** (`en`, `de`, `es`, `fr`). `apps/web/src/locales.test.ts` fails the build on any key-set drift. Help articles are English-only.
- **`apps/web` has no `jest-dom`.** Use plain assertions (`expect(x.textContent).toContain(...)`, `expect(x).toBeTruthy()`), never `toBeInTheDocument()`. Do not install it.
- **`userEvent` IS installed** and is the correct tool for anything a disabled/pointer-state check depends on; `fireEvent.click` fires handlers the browser would suppress.
- **`dotnet test --filter "Name=X"` does not work in this repo.** Use `--filter "FullyQualifiedName~X"`.
- **Never `git add -A`.** Stage explicit paths - the repo carries hundreds of untracked agent scratch files.
- **Split queries are the app-wide default.** Do not add `.AsSplitQuery()`.
- Target version: **0.239.0** (functional enhancement: Minor +1, Build reset).
- Deployment surface: **server redeploy only** (web + API). No desktop release.
- **No GitHub issue needed** - CLAUDE.md scopes the issue-first rule to bug fixes; this is a feature.

---

## Design decisions locked before implementation

Three places this plan deliberately diverges from, or resolves an ambiguity in, the handoff document (`Model picker redesign options.zip` → `design_handoff_model_picker/README.md`). Do not "fix" these back.

**1. The briefcase will appear on every row on day one, and that is accepted (option A).**
`LlmDefaultsOptions.ToolsSupported` defaults to **`true`** ([LlmDefaultsOptions.cs:80](../../../src/Diariz.Api/Configuration/LlmDefaultsOptions.cs:80)), and a read of the live database found that **none** of the nine configured models overrides `tools_supported` on any layer. So every model resolves `supportsTools: true` until an administrator turns it off. The icon is truthful - `tools_supported: true` means the platform really does offer that model tools - it is simply low-variance until the data is curated. The user chose this over shipping the eye alone. Do **not** backfill `tools_supported: false` (nothing in the platform knows which models can't call tools) and do **not** change the app default (that would stop the platform sending tools on every call for every group).

**2. The model name gets a `max-w` cap, against the handoff's "no ellipsis ever".**
The handoff specifies `flex-shrink: 0` and no `text-overflow` on the name. Taken literally that reintroduces the exact bug fixed in **0.232.1 / PR #558**: an imported slug-name is whatever the endpoint calls it, and a long one overflowed the menu, gave it a sideways scrollbar, and pushed the context length onto an invisible second line (measured at the time: 421px of scroll width in a 241px box). The name therefore gets `shrink-0 truncate max-w-[55%]` - at realistic name lengths it never truncates, which satisfies the design's intent, and a pathological name ellipsises instead of destroying the menu. The row keeps its `title={m.label}` so the full name stays reachable.

**3. Arrow-key navigation is out of scope.**
The handoff says "preserve the current menu's focus handling; arrow keys move between rows". The current menu handles **only Escape** - there is no arrow-key handling to preserve. "Preserve" therefore means "change nothing". Do not add it.

---

## File Structure

**Backend - create**
- `src/Diariz.Domain/Migrations/<timestamp>_AddLlmModelDescription.cs` (+ `.Designer.cs`, + snapshot update) - the one new column.

**Backend - modify**
- [src/Diariz.Domain/Entities/LlmModel.cs](../../../src/Diariz.Domain/Entities/LlmModel.cs) - `Description` property.
- [src/Diariz.Domain/DiarizDbContext.cs:666](../../../src/Diariz.Domain/DiarizDbContext.cs:666) - `HasMaxLength(200)` in the `LlmModel` block.
- [src/Diariz.Api/Contracts/ApiDtos.cs:739](../../../src/Diariz.Api/Contracts/ApiDtos.cs:739) - `ChatModelDto` gains `Description`, `SupportsTools`; [:929](../../../src/Diariz.Api/Contracts/ApiDtos.cs:929) `LlmModelDto` and `LlmModelUpsert` gain `Description`.
- [src/Diariz.Api/Controllers/LlmModelsController.cs](../../../src/Diariz.Api/Controllers/LlmModelsController.cs) - persist on Create/Update, return from `ToDto`.
- [src/Diariz.Api/Services/Llm/ChatModelCatalog.cs](../../../src/Diariz.Api/Services/Llm/ChatModelCatalog.cs) - `ChatModelOption` record + `SupportsTools` resolver.
- [src/Diariz.Api/Controllers/ChatModelsController.cs](../../../src/Diariz.Api/Controllers/ChatModelsController.cs) - projection.

**Backend - tests**
- `tests/Diariz.Api.IntegrationTests/LlmModelSchemaTests.cs` - column round-trip.
- `tests/Diariz.Api.Tests/LlmModelsControllerTests.cs` - description persistence.
- `tests/Diariz.Api.Tests/Llm/ChatModelCatalogTests.cs` - `SupportsTools` layer walk, `Description` passthrough.
- `tests/Diariz.Api.Tests/ChatModelsControllerTests.cs` - DTO shape.

**Web - modify**
- [apps/web/src/lib/types.ts:1505](../../../apps/web/src/lib/types.ts:1505) `LlmModel`, `:1547` `LlmModelUpsert`, `:1559` `ChatModelOption`.
- [apps/web/src/components/llmmodels/ModelEditorDrawer.tsx](../../../apps/web/src/components/llmmodels/ModelEditorDrawer.tsx) - state, `dirty`, `save()`, `ConnectionPanel`.
- [apps/web/src/components/ChatModelPicker.tsx](../../../apps/web/src/components/ChatModelPicker.tsx) - the menu rebuild. Stays one file: it is ~160 lines today and ~260 after, all one component's markup plus three local icons, matching how `ChatPanel.tsx` and `detail/icons.tsx` already inline their glyphs.
- `apps/web/src/locales/{en,de,es,fr}/{chat,account}.json`.

**Web - tests**
- `apps/web/src/components/llmmodels/ModelEditorDrawer.test.tsx`
- `apps/web/src/components/ChatModelPicker.test.tsx`

**Docs / release**
- `version.json` + 4 mirrors, `apps/web/src/lib/releases.ts`, `README.md`, `docs/features.md`, `docs/Data_Schema.md`, `apps/web/src/content/help/en/ai-model-settings.md`.
- `integrations/n8n-nodes-diariz/nodes/Diariz/generated/openapi.snapshot.json` (regenerated).

---

## Task 1: The `Description` column

**Files:**
- Modify: `src/Diariz.Domain/Entities/LlmModel.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs:666-677`
- Create: `src/Diariz.Domain/Migrations/<timestamp>_AddLlmModelDescription.cs`
- Test: `tests/Diariz.Api.IntegrationTests/LlmModelSchemaTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `LlmModel.Description` - `string?`, max length 200, nullable in Postgres. Every later task reads or writes this property.

- [ ] **Step 1: Write the failing test**

Append to `tests/Diariz.Api.IntegrationTests/LlmModelSchemaTests.cs`, inside the class:

```csharp
    /// <summary>The picker's per-model description. Round-tripped against real Postgres rather than the
    /// in-memory provider because the length cap and the nullability are schema facts, and the in-memory
    /// provider enforces neither - a unit test would pass with no column at all.</summary>
    [Fact]
    public async Task Description_round_trips_and_is_optional()
    {
        await using var db = fx.CreateDbContext();
        var described = NewModel();
        described.Description = new string('x', 200);
        var undescribed = NewModel();
        db.LlmModels.AddRange(described, undescribed);
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        Assert.Equal(new string('x', 200), (await read.LlmModels.FindAsync(described.Id))!.Description);
        Assert.Null((await read.LlmModels.FindAsync(undescribed.Id))!.Description);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~Description_round_trips"
```

Expected: **compile error** - `'LlmModel' does not contain a definition for 'Description'`. A compile failure is a valid red here: the property genuinely does not exist yet.

- [ ] **Step 3: Add the property**

In `src/Diariz.Domain/Entities/LlmModel.cs`, immediately after the `DisplayName` property:

```csharp
    /// <summary>A short phrase shown beside the name in the chat model picker, e.g. "Use this for most
    /// chats". Null means the row simply has no description - never a generated one: a sentence nobody
    /// wrote would read as advice the platform is giving, which is exactly what it is not.</summary>
    public string? Description { get; set; }
```

- [ ] **Step 4: Configure the column**

In `src/Diariz.Domain/DiarizDbContext.cs`, in the `builder.Entity<LlmModel>` block, directly under the `DisplayName` line:

```csharp
            e.Property(m => m.Description).HasMaxLength(200);
```

- [ ] **Step 5: Generate the migration**

```bash
dotnet ef migrations add AddLlmModelDescription --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Open the generated `Up` and confirm it is exactly one `AddColumn<string>` with `nullable: true` and `maxLength: 200` on table `LlmModels`, and that `Down` is the matching `DropColumn`. If it contains anything else, an unrelated model change has been picked up - stop and investigate rather than committing it.

**Do NOT bump `MaintenanceController.CurrentFormat`.** The column is additive and nullable, so an older backup restores and migrates forward cleanly.

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~Description_round_trips"
```

Expected: PASS. (Needs Docker.)

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Domain/Entities/LlmModel.cs src/Diariz.Domain/DiarizDbContext.cs src/Diariz.Domain/Migrations tests/Diariz.Api.IntegrationTests/LlmModelSchemaTests.cs
git commit -m "feat: add a per-model Description column"
```

---

## Task 2: Administrator API carries the description

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs:929-937`
- Modify: `src/Diariz.Api/Controllers/LlmModelsController.cs` (Create, Update, ToDto)
- Test: `tests/Diariz.Api.Tests/LlmModelsControllerTests.cs`

**Interfaces:**
- Consumes: `LlmModel.Description` (Task 1).
- Produces: `LlmModelDto(..., string? DisplayName = null, bool ChatEnabled = false, string? Description = null)` and `LlmModelUpsert(..., string? DisplayName = null, string? Description = null)`. Task 5 (web) binds to the camelCase `description` on both.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Diariz.Api.Tests/LlmModelsControllerTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task Create_stores_the_description()
    {
        using var db = TestDb.Create();

        var created = await Build(db).Create(new LlmModelUpsert(
            "m", "http://llm/v1", null, 8192, new Dictionary<string, string>(),
            Description: "Use this for most chats"));

        Assert.Equal("Use this for most chats", Assert.IsType<LlmModelDto>(created.Value).Description);
        Assert.Equal("Use this for most chats", db.LlmModels.Single().Description);
    }

    [Fact]
    public async Task Update_clears_the_description_when_it_is_blank()
    {
        // One representation of absent, the same rule DisplayName follows: "" and "   " both become null,
        // so the picker has a single thing to test for rather than rendering an empty flex gap that came
        // from a stored space.
        using var db = TestDb.Create();
        var created = await Build(db).Create(new LlmModelUpsert(
            "m", "http://llm/v1", null, 8192, new Dictionary<string, string>(), Description: "Fast"));
        var id = Assert.IsType<LlmModelDto>(created.Value).Id;

        await Build(db).Update(id, new LlmModelUpsert(
            "m", "http://llm/v1", null, 8192, new Dictionary<string, string>(), Description: "   "));

        Assert.Null(db.LlmModels.Single().Description);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmModelsControllerTests.Create_stores_the_description|FullyQualifiedName~LlmModelsControllerTests.Update_clears_the_description"
```

Expected: **compile error** - `LlmModelUpsert` has no `Description` parameter.

- [ ] **Step 3: Extend the DTOs**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, replace the two records (keep the surrounding XML docs, and add the sentence shown):

```csharp
public record LlmModelDto(Guid Id, string Name, string ApiBase, bool HasApiKey, int ContextLength,
    Dictionary<string, string> Parameters, string? DisplayName = null, bool ChatEnabled = false,
    string? Description = null);
```

```csharp
public record LlmModelUpsert(string Name, string ApiBase, string? ApiKey, int ContextLength,
    Dictionary<string, string> Parameters, string? DisplayName = null, string? Description = null);
```

Add to the `LlmModelUpsert` XML doc, after the existing `ChatEnabled` paragraph:

```
/// <c>Description</c> IS a field here, unlike <c>ChatEnabled</c>: the editor drawer shows the control, so a
/// save posts a value the administrator can actually see on screen.
```

Both are trailing optional parameters, so no existing construction site breaks.

- [ ] **Step 4: Persist and return it**

In `src/Diariz.Api/Controllers/LlmModelsController.cs`:

In `Create`, in the `new LlmModel { ... }` initialiser, after `DisplayName = Trim(req.DisplayName),`:

```csharp
            Description = Trim(req.Description),
```

In `Update`, after `model.DisplayName = Trim(req.DisplayName);`:

```csharp
        model.Description = Trim(req.Description);
```

In `ToDto`, change the final argument line to:

```csharp
        m.DisplayName, m.ChatEnabled, m.Description);
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmModelsControllerTests"
```

Expected: PASS, whole class green.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/LlmModelsController.cs tests/Diariz.Api.Tests/LlmModelsControllerTests.cs
git commit -m "feat: administrators can set a model description"
```

---

## Task 3: The catalog resolves `SupportsTools` and passes `Description` through

**Files:**
- Modify: `src/Diariz.Api/Services/Llm/ChatModelCatalog.cs`
- Test: `tests/Diariz.Api.Tests/Llm/ChatModelCatalogTests.cs`

**Interfaces:**
- Consumes: `LlmModel.Description` (Task 1), `LlmParameterLayers.Resolve` / `LlmParameterStack.For` (existing).
- Produces: `ChatModelOption(Guid Id, string Label, string Name, int ContextLength, bool IsDefault, bool SupportsImages, bool SupportsTools, string? Description)` - Task 4 projects this into `ChatModelDto`.

**Why `SupportsTools` is resolved and not stored:** `tools_supported` is a parameter, not a column. The class's own summary warns that a private second layer walk "could grow its own layer walk, pass every test above, and still offer a model as vision-capable that `LlmSettingsResolver` then rejects". The tools flag has exactly the same hazard, so it goes through the identical call.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Diariz.Api.Tests/Llm/ChatModelCatalogTests.cs`, inside the class, after the existing `SupportsImages_*` tests (the `Seed`, `AssignChat`, `SetParameters` and `Catalog` helpers already exist above them):

```csharp
    // ---- SupportsTools: the flag the picker's briefcase icon reports ----

    [Fact]
    public async Task SupportsTools_IsTrueWhenNoLayerMentionsIt()
    {
        // The shipped app default is TRUE, the opposite of images. That is deliberate and load-bearing:
        // before parameters were platform-managed the platform always offered tools and let the model
        // ignore them, so defaulting to false here would silently stop tool calls on every existing
        // deployment. The consequence for the picker is that the briefcase shows on every model until an
        // administrator turns it off - low signal, not false.
        using var db = TestDb.Create();
        var chat = Seed(db, "plain-model");
        AssignChat(db, chat.Id);

        Assert.True(Assert.Single(await Catalog(db).ListAsync()).SupportsTools);
    }

    [Fact]
    public async Task SupportsTools_ComesFromTheChatOverride()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "no-tools-model");
        AssignChat(db, chat.Id);
        SetParameters(db, chat, LlmCallGroup.Chat, """{"tools_supported":false}""");

        Assert.False(Assert.Single(await Catalog(db).ListAsync()).SupportsTools);
    }

    [Fact]
    public async Task SupportsTools_FallsThroughToTheModelBaseLayer()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "no-tools-model");
        AssignChat(db, chat.Id);
        SetParameters(db, chat, LlmCallGroup.ModelBase, """{"tools_supported":false}""");

        Assert.False(Assert.Single(await Catalog(db).ListAsync()).SupportsTools);
    }

    [Fact]
    public async Task SupportsTools_ChatOverrideOfTrueBeatsAModelBaseFalse()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "model");
        AssignChat(db, chat.Id);
        SetParameters(db, chat, LlmCallGroup.ModelBase, """{"tools_supported":false}""");
        SetParameters(db, chat, LlmCallGroup.Chat, """{"tools_supported":true}""");

        Assert.True(Assert.Single(await Catalog(db).ListAsync()).SupportsTools);
    }

    [Fact]
    public async Task SupportsTools_HonoursThePlatformDefaultLayer()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "model");
        AssignChat(db, chat.Id);

        var defaults = new LlmDefaultsOptions { ToolsSupported = false };

        Assert.False(Assert.Single(await Catalog(db, defaults).ListAsync()).SupportsTools);
    }

    [Fact]
    public async Task Description_is_carried_through_verbatim()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "model");
        chat.Description = "Use this for most chats";
        db.SaveChanges();
        AssignChat(db, chat.Id);

        Assert.Equal("Use this for most chats", Assert.Single(await Catalog(db).ListAsync()).Description);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatModelCatalogTests"
```

Expected: **compile error** - `ChatModelOption` has no `SupportsTools` / `Description`.

- [ ] **Step 3: Extend `ChatModelOption`**

In `src/Diariz.Api/Services/Llm/ChatModelCatalog.cs`, replace the record:

```csharp
public sealed record ChatModelOption(
    Guid Id, string Label, string Name, int ContextLength, bool IsDefault, bool SupportsImages,
    bool SupportsTools, string? Description);
```

Add to the record's XML doc, after the existing `SupportsImages` paragraph:

```
/// <para><see cref="SupportsTools"/> is the resolved <c>tools_supported</c> parameter for the Chat group,
/// through the SAME walk as <see cref="SupportsImages"/>. Its app default is <b>true</b>, so it reads true
/// for every model an administrator has not explicitly turned it off on.</para>
```

- [ ] **Step 4: Project the two new values**

In `ListAsync`, replace the `.Select(...)` with the block below. The layer walk runs **once** per model and both flags are read off the one result - calling `Resolved(...)` twice would walk every layer twice for no reason, and would let the two flags drift apart if the walk ever became non-deterministic.

```csharp
            .Select(m =>
            {
                var p = Resolved(m, platform);
                return new ChatModelOption(
                    m.Id, m.Label, m.Name, m.ContextLength, m.Id == defaultId,
                    p.ImagesSupported, p.ToolsSupported, m.Description);
            })
```

- [ ] **Step 5: Replace the private resolver**

Replace the whole `SupportsImages` private method at the bottom of the class with:

```csharp
    /// <summary>This model's resolved Chat-group parameters.
    ///
    /// <para>Resolved through <see cref="LlmParameterStack"/> - the SAME walk
    /// <see cref="LlmSettingsResolver"/> performs - rather than a second one written here. A private copy
    /// would pass every test about this class and still let the picker offer a capability the pipeline
    /// refuses, which is the failure this type's summary warns about.</para></summary>
    private LlmParameters Resolved(LlmModel model, PlatformSettings? platform) =>
        LlmParameterLayers.Resolve(LlmParameterStack.For(model, LlmCallGroup.Chat, defaults.Value, platform));
```

- [ ] **Step 6: Run tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatModelCatalogTests"
```

Expected: PASS, including the pre-existing `SupportsImages_*` tests and the parameterised "picker and pipeline must not merely happen to agree" theory.

- [ ] **Step 7: Build the whole solution**

Other files construct `ChatModelOption`; a unit-test-only run will not catch a break in the integration project.

```bash
dotnet build Diariz.slnx
```

Expected: 0 errors. Fix any construction site the new positional parameters broke.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Services/Llm/ChatModelCatalog.cs tests/Diariz.Api.Tests/Llm/ChatModelCatalogTests.cs
git commit -m "feat: the chat catalog resolves tool support and carries the description"
```

---

## Task 4: `ChatModelDto` exposes both fields

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs:739-740`
- Modify: `src/Diariz.Api/Controllers/ChatModelsController.cs`
- Modify: `integrations/n8n-nodes-diariz/nodes/Diariz/generated/openapi.snapshot.json` (regenerated, not hand-edited)
- Test: `tests/Diariz.Api.Tests/ChatModelsControllerTests.cs`

**Interfaces:**
- Consumes: `ChatModelOption.SupportsTools` / `.Description` (Task 3).
- Produces: `ChatModelDto(Guid Id, string Label, string Name, int ContextLength, bool IsDefault, bool SupportsImages, bool SupportsTools, string? Description)`. Serialised camelCase: Task 6/7 read `supportsTools` and `description`.

- [ ] **Step 1: Write the failing test**

Append to `tests/Diariz.Api.Tests/ChatModelsControllerTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task Returns_the_description_and_the_capability_flags()
    {
        using var db = TestDb.Create();
        var m = Seed(db, "m", chatEnabled: true);
        m.Description = "Use this for most chats";
        db.SaveChanges();

        var result = await Build(db).List();
        var dto = Assert.Single(Assert.IsType<List<ChatModelDto>>(result.Value));

        Assert.Equal("Use this for most chats", dto.Description);
        // The app default for tools is true and for images false - the picker's two icons therefore start
        // out asymmetric, which is the honest reflection of the resolved parameters.
        Assert.True(dto.SupportsTools);
        Assert.False(dto.SupportsImages);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatModelsControllerTests.Returns_the_description"
```

Expected: **compile error** - `ChatModelDto` has no `SupportsTools` / `Description`.

- [ ] **Step 3: Extend the DTO**

In `src/Diariz.Api/Contracts/ApiDtos.cs`:

```csharp
public record ChatModelDto(
    Guid Id, string Label, string Name, int ContextLength, bool IsDefault, bool SupportsImages,
    bool SupportsTools, string? Description);
```

Add to its XML doc, after the `SupportsImages` paragraph:

```
/// <paramref name="SupportsTools"/> is whether the platform will offer this model its chat tools. It is a
/// resolved parameter with an app default of <b>true</b>, so it reads true unless an administrator has
/// turned it off. <paramref name="Description"/> is the administrator's short phrase for the picker, or
/// null where none is set.
```

- [ ] **Step 4: Project it**

In `src/Diariz.Api/Controllers/ChatModelsController.cs`, replace the `.Select(...)`:

```csharp
            .Select(o => new ChatModelDto(
                o.Id, o.Label, o.Name, o.ContextLength, o.IsDefault, o.SupportsImages,
                o.SupportsTools, o.Description))
```

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatModelsControllerTests"
```

Expected: PASS.

- [ ] **Step 6: Regenerate the OpenAPI snapshot**

`/api/chat/models` **is** in the published document (unlike `api/admin/*`, which is excluded), so the snapshot changes. The snapshot test rewrites its own file, so run 1 fails and run 2 passes with no code change - that is expected, not a flake.

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshot"
```

Run it a second time and confirm PASS, then inspect the diff on `openapi.snapshot.json` and confirm it contains **only** the two new `ChatModelDto` properties.

- [ ] **Step 7: Regenerate the n8n node**

The generated `index.ts` carries endpoints, not response fields, so it should come back unchanged - but it does **not** self-heal, and a stale one reds the "n8n community node" check.

```bash
cd integrations/n8n-nodes-diariz && npm run generate
```

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/ChatModelsController.cs tests/Diariz.Api.Tests/ChatModelsControllerTests.cs integrations/n8n-nodes-diariz
git commit -m "feat: the chat models endpoint returns description and tool support"
```

---

## Task 5: The Description field in the model editor

**Files:**
- Modify: `apps/web/src/lib/types.ts` (`LlmModel`, `LlmModelUpsert`)
- Modify: `apps/web/src/components/llmmodels/ModelEditorDrawer.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json`
- Test: `apps/web/src/components/llmmodels/ModelEditorDrawer.test.tsx`

**Interfaces:**
- Consumes: the `description` field on `LlmModelDto`/`LlmModelUpsert` (Task 2).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

Append to `apps/web/src/components/llmmodels/ModelEditorDrawer.test.tsx`, inside the top-level `describe`. These deliberately mirror the two `display name` tests already in the file (around line 166) line for line - same `fireEvent.change` + `fireEvent.click(/^save$/i)` + `vi.waitFor` shape, same assertion style - because description follows exactly the same three-state rule as displayName and a reader should be able to see that at a glance.

```tsx
  it("saves a description from the connection panel", async () => {
    api.updateModel.mockResolvedValue(MODELS[0]);
    open(MODELS[0]);

    fireEvent.change(screen.getByLabelText(/description/i), { target: { value: "Use this for most chats" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(api.updateModel.mock.calls[0][1].description).toBe("Use this for most chats");
  });

  it("sends a blank description as null, so the row simply has none", async () => {
    // "" and null would be two spellings of the same state; the server stores one, so the client sends
    // one. A stored empty string would render as a gap in the picker that nobody asked for.
    api.updateModel.mockResolvedValue(MODELS[0]);
    open({ ...MODELS[0], description: "Fast" });

    fireEvent.change(screen.getByLabelText(/description/i), { target: { value: "   " } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(api.updateModel.mock.calls[0][1].description).toBeNull();
  });

  it("treats an edited description as unsaved work", async () => {
    // The drawer persists nothing until Save, so a description typed and then dismissed is a real loss -
    // but the warning only fires when `dirty` knows about the field, which is the thing being tested.
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const onClose = vi.fn();
    open(MODELS[0], { onClose });

    fireEvent.change(screen.getByLabelText(/description/i), { target: { value: "Fast" } });
    fireEvent.click(screen.getByRole("button", { name: /^close$/i }));

    expect(confirm).toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
    confirm.mockRestore();
  });
```

Two details that will bite otherwise: `open`'s signature is `open(model, props)`, so the second test passes a spread model object and the third passes `onClose` as an override. And `/description/i` must not also match the **hint** text under the input - the hint is a `<span>`, not a label, so `getByLabelText` ignores it, but if you reword the hint to start with "Description" a future `getByLabelText` call could still resolve ambiguously.

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd apps/web && npx vitest run src/components/llmmodels/ModelEditorDrawer.test.tsx
```

Expected: FAIL on all three - `Unable to find a label with the text of: /description/i`.

- [ ] **Step 3: Extend the web types**

In `apps/web/src/lib/types.ts`, in `LlmModel`, after `displayName`:

```ts
  /// A short phrase shown beside the name in the chat model picker. Null means the model has none.
  description: string | null;
```

In `LlmModelUpsert`, after `displayName`:

```ts
  /// Blank is sent as null, so the picker has a single thing to test for rather than an empty string.
  description?: string | null;
```

`description` is **required** on `LlmModel` (it is a field the API always returns) and optional on `LlmModelUpsert`, so every existing `LlmModel` fixture stops compiling. There are four, all of which need `description: null` added to each model literal:

- `apps/web/src/components/llmmodels/ModelEditorDrawer.test.tsx`
- `apps/web/src/components/llmmodels/RoutingMatrix.test.tsx`
- `apps/web/src/components/SettingsModal.test.tsx`
- `apps/web/src/pages/LlmModels.test.tsx`

**Nothing will catch a missed one.** `tsconfig.json` excludes `src/**/*.test.ts(x)` from the build's `tsc`, and vitest does not typecheck either - so a fixture with no `description` neither fails to compile nor fails a test (the field is only read, and `undefined` is falsy). Work through the four files by hand and confirm with `grep -rn "displayName:" apps/web/src --include=*.test.tsx` that every fixture literal now carries `description` too.

- [ ] **Step 4: Wire the state, the dirty check and the payload**

In `ModelEditorDrawer.tsx`:

After the `displayName` state declaration:

```tsx
  const [description, setDescription] = useState(model?.description ?? "");
```

In the `dirty` expression, after the `displayName` clause:

```tsx
    description !== (model?.description ?? "") ||
```

In `save()`'s `payload`, after the `displayName` line:

```tsx
        // Blank means "no description". Sent as null rather than "" for the same reason displayName is.
        description: description.trim() || null,
```

In the `<ConnectionPanel ... />` call, after the `displayName` line:

```tsx
            description={description} setDescription={setDescription}
```

- [ ] **Step 5: Add the field to `ConnectionPanel`**

Add to the destructured props and the type:

```tsx
  description, setDescription,
```

```tsx
  description: string;
  setDescription: (v: string) => void;
```

Add this `<label>` between the Display name label and the Endpoint label:

```tsx
      <label className="block text-[11.5px]">
        <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsDescription")}</span>
        <input
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          maxLength={200}
          placeholder={t("llmModelsDescriptionPlaceholder")}
          className={field}
        />
        <span className="mt-0.5 block text-[10.5px] text-gray-400 dark:text-gray-500">{t("llmModelsDescriptionHint")}</span>
      </label>
```

`getByLabelText` resolves through the wrapping `<label>`, exactly as the existing fields do.

- [ ] **Step 6: Add the locale keys to all four catalogs**

`apps/web/src/locales/en/account.json`:

```json
  "llmModelsDescription": "Description",
  "llmModelsDescriptionHint": "Shown beside the name in the chat model picker. About 30 characters reads best.",
  "llmModelsDescriptionPlaceholder": "Use this for most chats",
```

`de/account.json`:

```json
  "llmModelsDescription": "Beschreibung",
  "llmModelsDescriptionHint": "Wird in der Chat-Modellauswahl neben dem Namen angezeigt. Etwa 30 Zeichen lesen sich am besten.",
  "llmModelsDescriptionPlaceholder": "Fuer die meisten Chats geeignet",
```

`es/account.json`:

```json
  "llmModelsDescription": "Descripción",
  "llmModelsDescriptionHint": "Se muestra junto al nombre en el selector de modelos del chat. Unos 30 caracteres se leen mejor.",
  "llmModelsDescriptionPlaceholder": "Úsalo para la mayoría de los chats",
```

`fr/account.json`:

```json
  "llmModelsDescription": "Description",
  "llmModelsDescriptionHint": "Affiché à côté du nom dans le sélecteur de modèles du chat. Environ 30 caractères se lisent le mieux.",
  "llmModelsDescriptionPlaceholder": "À utiliser pour la plupart des discussions",
```

Keep each catalog's existing key ordering convention (they are alphabetical around `llmModels*`). **These files are UTF-8** - edit them with the Write/Edit tools, not by piping through a shell, which mangles accented characters on this machine.

- [ ] **Step 7: Run tests to verify they pass**

```bash
cd apps/web && npx vitest run src/components/llmmodels/ModelEditorDrawer.test.tsx src/locales.test.ts
```

Expected: PASS, both files. `locales.test.ts` is the gate that catches a key added to `en` but missed in one of the other three.

- [ ] **Step 8: Typecheck**

```bash
cd apps/web && npm run build
```

Expected: no TypeScript errors. Fix any fixture that now needs `description`.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/components/llmmodels/ModelEditorDrawer.tsx apps/web/src/components/llmmodels/ModelEditorDrawer.test.tsx apps/web/src/locales
git commit -m "feat: a Description field in the AI model editor"
```

---

## Task 6: `formatContext` - binary-K context formatting

**Files:**
- Modify: `apps/web/src/components/ChatModelPicker.tsx`
- Test: `apps/web/src/components/ChatModelPicker.test.tsx`

**Interfaces:**
- Consumes: nothing.
- Produces: `export function formatContext(tokens: number): string` - Task 7 renders its output in the chip.

- [ ] **Step 1: Write the failing test**

Add to `apps/web/src/components/ChatModelPicker.test.tsx`. Change the component import line to also pull the named export:

```tsx
import ChatModelPicker, { formatContext } from "./ChatModelPicker";
```

Then add a sibling `describe` at the end of the file:

```tsx
describe("formatContext", () => {
  it("rounds on 1024, not 1000", () => {
    // 131,072 is 128 binary K. Rounding on 1000 would print "131K", a number that matches nothing the
    // model's documentation says and that no one would recognise as its context window.
    expect(formatContext(131072)).toBe("128K");
    expect(formatContext(262144)).toBe("256K");
    expect(formatContext(8192)).toBe("8K");
  });

  it("switches to M at a megabyte of tokens", () => {
    expect(formatContext(1048576)).toBe("1M");
    expect(formatContext(1572864)).toBe("1.5M");
  });

  it("rounds an odd window to the nearest K rather than showing a fraction", () => {
    // Imported models routinely report a window that is not a power of two. The chip has room for three
    // or four characters, and the exact figure is one hover away.
    expect(formatContext(200000)).toBe("195K");
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd apps/web && npx vitest run src/components/ChatModelPicker.test.tsx -t formatContext
```

Expected: FAIL - `formatContext is not a function`.

- [ ] **Step 3: Implement it**

Add to `apps/web/src/components/ChatModelPicker.tsx`, below the `MARGIN` constant:

```tsx
/// A context window as binary K or M: 131,072 reads "128K", not "131K".
///
/// Rounded on 1024 deliberately. Model documentation quotes these windows in binary units, so dividing by
/// 1000 would print a number that matches nothing the user has read anywhere else. The exact count is never
/// lost - it is on the chip's tooltip.
export function formatContext(tokens: number): string {
  if (tokens >= 1024 * 1024) return `${+(tokens / 1024 / 1024).toFixed(1)}M`;
  return `${Math.round(tokens / 1024)}K`;
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
cd apps/web && npx vitest run src/components/ChatModelPicker.test.tsx -t formatContext
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/ChatModelPicker.tsx apps/web/src/components/ChatModelPicker.test.tsx
git commit -m "feat: format a context window as binary K"
```

---

## Task 7: The menu rebuild

**Files:**
- Modify: `apps/web/src/components/ChatModelPicker.tsx`
- Modify: `apps/web/src/lib/types.ts` (`ChatModelOption`)
- Modify: `apps/web/src/locales/{en,de,es,fr}/chat.json`
- Test: `apps/web/src/components/ChatModelPicker.test.tsx`

**Interfaces:**
- Consumes: `formatContext` (Task 6), `supportsTools` / `description` on the wire (Task 4).
- Produces: the finished menu. Nothing later depends on its internals.

- [ ] **Step 1: Extend `ChatModelOption` and the test fixture**

In `apps/web/src/lib/types.ts`, in `ChatModelOption`, after `supportsImages`:

```ts
  /// Whether the platform will offer this model its chat tools. A RESOLVED parameter whose app default is
  /// true, so it reads true for every model an administrator has not turned it off on - the briefcase icon
  /// is therefore near-universal until the flags are curated.
  supportsTools: boolean;
  /// The administrator's short phrase for this model, or null where none is set.
  description: string | null;
```

There are two `ChatModelOption` fixtures: `ChatModelPicker.test.tsx` (below) and **`apps/web/src/components/ChatPanel.test.tsx` around lines 103-104**, whose two model literals each need `supportsTools` and `description` added. **Neither the build nor vitest typechecks test files** (`tsconfig.json` excludes `src/**/*.test.ts(x)`), so a missed fixture stays silently green - `supportsTools: undefined` simply renders no briefcase. Fix both by hand and verify with `grep -rn "supportsImages" apps/web/src --include=*.test.tsx`.

In `ChatModelPicker.test.tsx`, replace the `MODELS` fixture:

```tsx
const MODELS: ChatModelOption[] = [
  {
    id: "a", label: "GPT OSS 20B", name: "openai/gpt-oss-20b", contextLength: 131072, isDefault: true,
    supportsImages: false, supportsTools: true, description: "Use this for most chats",
  },
  {
    id: "b", label: "QWEN 3.8", name: "qwen3.8-27b@q4_k_xl", contextLength: 200000, isDefault: false,
    supportsImages: true, supportsTools: false, description: null,
  },
];
```

- [ ] **Step 2: Write the failing tests**

Replace the existing `"lists each model's label with its context length in brackets"` test and the existing `"marks the models that can read images, and only those"` test with the block below, and add the rest. Every other test in the file stays as it is.

```tsx
  it("shows each model's context window as binary K, with the exact count on hover", () => {
    // "131,072 ctx" was accurate and unreadable: it is the longest thing on the row and the least
    // glanceable. 128K is the number the model's own documentation quotes.
    open();

    const row = screen.getByRole("menuitemradio", { name: /GPT OSS 20B/ });
    const chip = within(row).getByText("128K");
    expect(chip.getAttribute("title")).toBe("131,072 tokens");
    expect(row.textContent).not.toContain("131,072 ctx");
  });

  it("shows the administrator's description beside the name", () => {
    open();

    expect(screen.getByRole("menuitemradio", { name: /GPT OSS 20B/ }).textContent)
      .toContain("Use this for most chats");
  });

  it("renders no description element for a model that has none", () => {
    // A model with no description gets empty flex space, not the word "null" and not a placeholder
    // sentence the platform made up.
    open();

    const row = screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ });
    expect(row.textContent).not.toContain("null");
    expect(row.textContent).not.toContain("undefined");
  });

  it("marks tool support and image support independently", () => {
    // The two capabilities are separate resolved parameters and a model can have either, both, or
    // neither. Asserting them together on one row would let a bug that ties them pass.
    open();

    const oss = screen.getByRole("menuitemradio", { name: /GPT OSS 20B/ });
    expect(within(oss).queryByRole("img", { name: "Calls tools" })).not.toBeNull();
    expect(within(oss).queryByRole("img", { name: "Reads images" })).toBeNull();

    const qwen = screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ });
    expect(within(qwen).queryByRole("img", { name: "Calls tools" })).toBeNull();
    expect(within(qwen).queryByRole("img", { name: "Reads images" })).not.toBeNull();
  });

  it("titles the menu and explains its icons", () => {
    // The icons are the only thing on a row that is not words. Without the legend a briefcase is a
    // guess, and the menu had no title at all before.
    open();

    const menu = screen.getByRole("menu");
    expect(menu.textContent).toContain("Answering model");
    expect(menu.textContent).toContain("Calls tools");
    expect(menu.textContent).toContain("Reads images");
  });

  it("keeps the title and the legend fixed while only the rows scroll", () => {
    // A single scroll container over the whole menu would scroll the legend out of sight exactly when a
    // long list makes it worth having.
    open();

    const menu = screen.getByRole("menu");
    const scroller = menu.querySelector("[data-testid='model-rows']");
    expect(scroller).not.toBeNull();
    expect(scroller!.className).toContain("overflow-y-auto");
    expect(menu.className).not.toContain("overflow-y-auto");
  });
```

Add `within` to the testing-library import at the top of the file:

```tsx
import { render, screen, fireEvent, within } from "@testing-library/react";
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
cd apps/web && npx vitest run src/components/ChatModelPicker.test.tsx
```

Expected: FAIL on the new tests - `Unable to find an element with the text: 128K`, and the old rows still rendering `(131,072 ctx)`.

- [ ] **Step 4: Add the locale keys to all four catalogs**

Remove `ctxSuffix` and `modelSupportsImages` from **all four** `chat.json` files (both are used only by this component, verified by grep), and add:

`en/chat.json`:

```json
  "modelPickerTitle": "Answering model",
  "modelCallsTools": "Calls tools",
  "modelReadsImages": "Reads images",
  "modelContextTokens": "{{tokens}} tokens",
```

`de/chat.json`:

```json
  "modelPickerTitle": "Antwortendes Modell",
  "modelCallsTools": "Nutzt Werkzeuge",
  "modelReadsImages": "Liest Bilder",
  "modelContextTokens": "{{tokens}} Tokens",
```

`es/chat.json`:

```json
  "modelPickerTitle": "Modelo que responde",
  "modelCallsTools": "Usa herramientas",
  "modelReadsImages": "Lee imágenes",
  "modelContextTokens": "{{tokens}} tokens",
```

`fr/chat.json`:

```json
  "modelPickerTitle": "Modèle qui répond",
  "modelCallsTools": "Utilise des outils",
  "modelReadsImages": "Lit les images",
  "modelContextTokens": "{{tokens}} jetons",
```

The placeholder is `{{tokens}}`, **not** `{{count}}`: i18next treats a variable named `count` as a plural selector and would demand `_one`/`_other` variants.

The header is uppercased by CSS (`uppercase`), not by the string, so each language keeps its own capitalisation rules.

- [ ] **Step 5: Widen the menu and parameterise the sparkle**

In `ChatModelPicker.tsx`, replace the `WIDTH` constant and its comment:

```tsx
/// Menu width in px. Wider than the 320px chat panel on purpose: the menu is portalled and fixed, so it
/// overhangs onto the workspace behind it, which is what gives the name, the description and the chip room
/// to sit on one line.
const WIDTH = 372;
```

Replace `SparkleIcon` at the bottom of the file so the header can render it at 15px while the toolbar button keeps 16:

```tsx
/// A four-point sparkle. Inline like ChatPanel's other toolbar icons rather than a dependency, and filled
/// rather than stroked so it still reads at 16px.
const SparkleIcon = ({ size = 16 }: { size?: number }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
    <path d="M12 2l2.2 6.1L20.4 10l-6.2 1.9L12 18l-2.2-6.1L3.6 10l6.2-1.9L12 2z" />
    <path d="M18.5 15l.9 2.5 2.6.8-2.6.8-.9 2.5-.9-2.5-2.6-.8 2.6-.8.9-2.5z" opacity="0.7" />
  </svg>
);
```

The existing call site is already `<SparkleIcon />`, so it is unaffected.

- [ ] **Step 6: Add the two capability icons**

Append to the bottom of `ChatModelPicker.tsx`:

```tsx
/// The same glyphs the app already uses, inlined rather than imported. `EyeIcon` in `detail/icons.tsx` is a
/// fixed-size ReactElement bound to `iconProps`, and the briefcase lives inside `MeetingTypeIcon`'s private
/// PATHS record - neither is sizable from here without refactoring a file this change has no business
/// touching. Same path data, local wrapper.
const BriefcaseIcon = ({ size = 14 }: { size?: number }) => (
  <svg width={size} height={size} viewBox="0 0 16 16" fill="none" stroke="currentColor"
       strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M2 6h12v7H2V6zM6 6V4h4v2M2 9h12" />
  </svg>
);

const EyeIcon = ({ size = 14 }: { size?: number }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
       strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7z" /><circle cx="12" cy="12" r="3" />
  </svg>
);

/// One capability marker. `role="img"` with a label rather than `aria-hidden`, so the capability joins the
/// row's accessible name - a screen reader user gets the same information the legend gives a sighted one.
const Capability = ({ label, children }: { label: string; children: React.ReactNode }) => (
  <span role="img" aria-label={label} title={label} className="inline-flex">
    {children}
  </span>
);
```

- [ ] **Step 7: Rebuild the menu body**

Replace the whole portalled `<div ref={menuRef} role="menu" ...>` element - from its opening tag to its closing `</div>` - with the following. Keep the long explanatory comment that sits immediately above it inside `createPortal(`; it documents why the menu is portalled and is still true.

```tsx
          <div
            ref={menuRef}
            role="menu"
            style={{ width: WIDTH, top: pos?.top ?? 0, left: pos?.left ?? 0, visibility: pos ? "visible" : "hidden" }}
            className="fixed z-30 overflow-hidden rounded-[10px] border border-slate-400 bg-white shadow-xl dark:border-gray-700 dark:bg-gray-800"
          >
            {/* A darker border than the app's usual gray-200, and the same colour on both dividers, so the
                menu reads as its own surface where it overhangs the workspace behind the panel. */}
            <div className="flex items-center gap-2 border-b border-slate-400 px-3 pb-2 pt-2.5 dark:border-white/[0.07]">
              <span className="text-indigo-500 dark:text-indigo-400">
                <SparkleIcon size={15} />
              </span>
              <span className="text-[11px] font-bold uppercase tracking-[0.09em] text-gray-700 dark:text-gray-200">
                {t("modelPickerTitle")}
              </span>
            </div>

            {/* Only the rows scroll. A scroll container over the whole menu would carry the title and the
                legend away exactly when a long list makes them worth having. */}
            <div data-testid="model-rows" className="max-h-64 overflow-y-auto py-1">
              {models.map((m) => {
                const isSelected = m.id === (selected?.id ?? null);
                return (
                  <button
                    key={m.id}
                    type="button"
                    role="menuitemradio"
                    aria-checked={isSelected}
                    onClick={() => {
                      onSelect(m.id);
                      setOpen(false);
                    }}
                    title={m.label}
                    className={`flex w-full items-center gap-2 px-3 py-[7px] text-left ${
                      isSelected
                        ? "bg-indigo-600/[0.07] dark:bg-indigo-400/[0.12]"
                        : "hover:bg-gray-100 dark:hover:bg-white/5"
                    }`}
                  >
                    {/* max-w is the one departure from the design, which asks for no ellipsis at all. An
                        imported name is whatever the endpoint calls it, and an unbounded one overflowed
                        the menu and pushed the context length out of sight - fixed in 0.232.1 (PR #558)
                        and not worth reintroducing. At realistic lengths it never truncates. */}
                    <span
                      className={`max-w-[55%] shrink-0 truncate pr-[3px] text-[13.5px] ${
                        isSelected
                          ? "font-semibold text-indigo-600 dark:text-indigo-300"
                          : "text-gray-700 dark:text-gray-200"
                      }`}
                    >
                      {m.label}
                    </span>

                    {/* The element that gives way when a row is tight. min-w-0 is load-bearing: a flex item
                        will not shrink below its content without it. */}
                    {m.description && (
                      <span className="min-w-0 flex-1 truncate text-[11px] text-gray-500 dark:text-gray-400">
                        {m.description}
                      </span>
                    )}
                    {!m.description && <span className="min-w-0 flex-1" />}

                    <span
                      className={`inline-flex shrink-0 items-center gap-[5px] ${
                        isSelected ? "text-gray-600 dark:text-gray-300" : "text-gray-500 dark:text-gray-500"
                      }`}
                    >
                      {m.supportsTools && (
                        <Capability label={t("modelCallsTools")}>
                          <BriefcaseIcon />
                        </Capability>
                      )}
                      {m.supportsImages && (
                        <Capability label={t("modelReadsImages")}>
                          <EyeIcon />
                        </Capability>
                      )}
                    </span>

                    <span
                      title={t("modelContextTokens", { tokens: m.contextLength.toLocaleString() })}
                      className={`shrink-0 rounded-[5px] px-1.5 py-0.5 text-[10.5px] font-bold tabular-nums ${
                        isSelected
                          ? "bg-indigo-600/[0.12] text-indigo-600 dark:bg-indigo-400/20 dark:text-indigo-300"
                          : "bg-gray-100 text-gray-500 dark:bg-white/[0.06] dark:text-gray-400"
                      }`}
                    >
                      {formatContext(m.contextLength)}
                    </span>
                  </button>
                );
              })}
            </div>

            {/* The icons are the only wordless thing on a row. */}
            <div className="flex items-center gap-2.5 whitespace-nowrap border-t border-slate-400 px-3 py-[7px] text-[10.5px] text-gray-500 dark:border-white/[0.07] dark:text-gray-500">
              <span className="inline-flex items-center gap-1">
                <BriefcaseIcon size={12} />
                {t("modelCallsTools")}
              </span>
              <span className="inline-flex items-center gap-1">
                <EyeIcon size={12} />
                {t("modelReadsImages")}
              </span>
            </div>
          </div>,
```

- [ ] **Step 8: Run tests to verify they pass**

```bash
cd apps/web && npx vitest run src/components/ChatModelPicker.test.tsx src/locales.test.ts
```

Expected: PASS - the new tests **and** every pre-existing one (portal escape, viewport clamp at the new 372px width, Escape close, pointer sequence, selection fallback, disabled while streaming).

The clamp test hard-codes `innerWidth: 340` and asserts `left === 8`; at 372px wide the menu no longer fits in 340px at all, so `Math.min(anchor.right - WIDTH, window.innerWidth - WIDTH - MARGIN)` goes negative and `Math.max(MARGIN, ...)` still returns 8. The assertion `left + width <= 340` will now **fail** (8 + 372 > 340). Update that final assertion to state what the placement actually guarantees, and say why:

```tsx
    // At 372px the menu is wider than a 340px window, so it cannot be fully on screen. The guarantee is
    // that its LEFT edge stays inside the margin - the names are at the left, and losing the right edge
    // costs at most the context chip. Clamping the width instead would reintroduce the squeeze the
    // overhang exists to avoid.
    expect(left).toBe(8);
```

- [ ] **Step 9: Typecheck**

```bash
cd apps/web && npm run build
```

Expected: no errors.

- [ ] **Step 10: Commit**

```bash
git add apps/web/src/components/ChatModelPicker.tsx apps/web/src/components/ChatModelPicker.test.tsx apps/web/src/lib/types.ts apps/web/src/locales
git commit -m "feat: redesign the chat model picker with capabilities and descriptions"
```

---

## Task 8: Verify the geometry in a real browser

jsdom computes no layout, so **no test in Task 7 proves the two claims the redesign actually rests on**: that the menu overhangs the panel without being clipped, and that a realistic model name is not truncated. A class-presence assertion about `truncate` says nothing about whether text fits. Measure it.

**Files:** none - this task changes no code unless it finds a defect.

- [ ] **Step 1: Start the stack and open the app**

Use `preview_start` (never Bash) for the dev server, sign in, open a recording so the chat panel is present.

- [ ] **Step 2: Open the picker and measure it**

With the menu open, run in the page:

```js
const menu = document.querySelector('[role="menu"]');
const panel = menu.getBoundingClientRect();
const rows = [...menu.querySelectorAll('[role="menuitemradio"]')];
JSON.stringify({
  menuWidth: panel.width,
  menuLeft: panel.left,
  menuRight: panel.right,
  viewport: window.innerWidth,
  bodyScrollWidth: document.body.scrollWidth,
  names: rows.map(r => {
    const n = r.firstElementChild;
    return { text: n.textContent, truncated: n.scrollWidth > n.clientWidth };
  }),
}, null, 2);
```

Expected: `menuWidth` is 372; `menuLeft` is at least 8 and less than the chat panel's left edge (the overhang is real); `menuRight` is at most `viewport`; `bodyScrollWidth` equals `window.innerWidth` (no horizontal scrollbar anywhere on the page); every `truncated` is `false` for the real model names on this platform.

- [ ] **Step 3: Check both themes and a narrow window**

`resize_window` to 1280x800 and to 768x1024, and toggle `colorScheme` light/dark. Re-open the picker each time and re-run the measurement. Confirm the menu never leaves the viewport and the page never gains a horizontal scrollbar.

- [ ] **Step 4: Screenshot both themes**

Take a `computer{action:"screenshot"}` in light and dark with the menu open, and compare against `design_handoff_model_picker/screenshots/1a-light.png` and `1a-dark.png`.

- [ ] **Step 5: Check the console**

`read_console_messages` with `onlyErrors: true`. Expected: nothing new.

- [ ] **Step 6: If anything failed, fix and re-verify**

Edit the source (never the live page), reload, repeat from Step 2. Commit any fix with its own test if the defect is testable in jsdom.

---

## Task 9: Release, docs and help

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`, `docs/Data_Schema.md`
- Modify: `apps/web/src/content/help/en/ai-model-settings.md`

- [ ] **Step 1: Bump the version to 0.239.0 in all five places**

`version.json` is canonical: `{ "version": "0.239.0" }`. Then the four mirrors - `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj` (`<Version>`), `integrations/n8n-nodes-diariz/package.json`. `versionMirrors.test.ts` fails the build if any drifts; the n8n one especially, because an npm version cannot be corrected once published.

- [ ] **Step 2: Find the real PR number**

```bash
gh pr list --state all --limit 1 --json number
```

Add 1 to that. Do **not** assume - Dependabot PRs and issues consume numbers from the same sequence, and nothing in the test suite catches a wrong `pr:` value.

- [ ] **Step 3: Add the release entry**

Insert at the top of `RELEASES` in `apps/web/src/lib/releases.ts`, replacing `<PR>` with the number from Step 2:

```ts
  {
    version: "0.239.0",
    date: "2026-08-21",
    pr: <PR>,
    headline: "The chat model list says what each model is for",
    summary:
      "The list behind the sparkle button in the chat panel was a bare set of lines - a name, then a raw number in brackets like \"(131,072 ctx)\". It had no title, it said nothing about what any model was good at, and the one thing it did tell you about a model's abilities it told you in words, at the end of the row, in a smaller font.\n\nIt now opens under a heading, and every model can carry a short description written by your administrator - \"Use this for most chats\", \"Very slow but more accurate\" - so choosing between them no longer means recognising a name. Two small icons mark what a model can do: a briefcase for the ones that can use the chat tools, an eye for the ones that can read screenshots. A legend along the bottom names both, so neither is a guess.\n\nThe context window is now written the way the models' own documentation writes it: 128K rather than 131,072. Hover it and the exact figure is still there.\n\nThe menu is wider than the chat panel and deliberately overhangs it, which is what gives the names and their descriptions room to sit on one line.\n\nDescriptions are set per model on the AI models page, in the Connection panel beside the display name. A model with none simply shows its name, as before.",
    added: [
      "A Description field on each AI model, shown beside the model's name in the chat model picker.",
      "The chat model list marks which models can use the chat tools and which can read images, with a legend naming both.",
      "The chat model list now has a heading, and shows each context window as 128K rather than 131,072 - the exact figure is on hover.",
    ],
    changed: [
      "The chat model list is wider and overhangs the chat panel, so long model names and their descriptions fit on one line.",
      "The briefcase marks the models the platform will offer its chat tools to. That setting defaults to on, so until you turn it off on the models that cannot call tools, every model in the list will show it.",
    ],
  },
```

- [ ] **Step 4: Update the About-box `CAPABILITIES`**

In the same file, in the `| **AI models** |` row, change `its endpoint, key and context window` to:

```
its endpoint, key, context window and a one-line description people see when they pick it
```

And in the `| **Chat over transcripts** |` row, change `pick which one answers and change it part-way through a conversation` to:

```
pick which one answers - the list names each model, describes what it is for, and marks which can use tools or read images - and change it part-way through a conversation
```

- [ ] **Step 5: Update the README Features table**

In `README.md`, in the `| **AI models (admin)** |` row, change `endpoint, key, context window` to `endpoint, key, context window, a one-line description shown to people picking a chat model`.

- [ ] **Step 6: Update `docs/features.md`**

Two edits, in lockstep with the README (never one without the other):

Around line 299, replace `display name with its context window in brackets` with:

```
display name, a short description written by an administrator, icons marking whether it can use the chat
tools or read images, and its context window as binary K (128K rather than 131,072, with the exact count on
hover). A legend along the foot of the menu names both icons. The menu is wider than the chat panel and
overhangs it so a long name and its description fit on one line.
```

Around line 313, after `and the model picker marks which models can read images`, add:

```
It marks tool support the same way, with a briefcase - though that parameter defaults to on, so every model
carries the briefcase until an administrator turns it off on the ones that cannot call tools.
```

Around line 667, in the editor-drawer paragraph, change `Connection details (name, endpoint, key, context window)` to `Connection details (name, display name, description, endpoint, key, context window)`.

- [ ] **Step 7: Update `docs/Data_Schema.md`**

Add a row to the `LlmModels` column table, directly under `DisplayName`:

```
| `Description` | varchar(200) null | a short phrase shown beside the name in the chat model picker, e.g. "Use this for most chats". Null means the model has none - never a generated one, since a sentence nobody wrote would read as advice the platform is giving |
```

Add a row to the end of the migration-history table:

```
| `AddLlmModelDescription` | `LlmModels.Description` (varchar(200) null) - the administrator's short phrase for a model, shown beside its name in the chat model picker. Additive and nullable, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
```

`docs/Overall_Synopsis_of_Platform.md` needs **no** edit: no component, contract, dependency, endpoint or deployment detail changed.

- [ ] **Step 8: Update the help article**

`apps/web/src/content/help/en/ai-model-settings.md` is **ASCII only**. Two edits:

In the "Configuring models" bullet list, after the **A display name** bullet:

```
- **A description**, optional. A short phrase telling people what the model is for - "Use this for most
  chats", "Slower but more accurate" - shown next to its name whenever someone picks a chat model. About
  thirty characters reads best; a longer one is shortened with an ellipsis. Leave it blank and the row
  simply shows the name.
```

In the "Parameters" section, after the sentence ending `whether the model supports tool calling or image input.`:

```
Those last two are the only parameters people other than administrators ever see. The chat model picker
marks each model with a briefcase when tool calling is on and an eye when image input is on, so a person
choosing a model can tell what it will be able to do. Tool calling **defaults to on**, so every model shows
the briefcase until you turn it off on the ones that cannot call tools.
```

- [ ] **Step 9: Run the full test suite**

```bash
dotnet test tests/Diariz.Api.Tests
```

```bash
cd apps/web && npm test
```

```bash
dotnet build Diariz.slnx
```

Expected: all green, **no warnings**. A passing run has pristine output. If `helpContent.test.ts` fails, an article's front-matter or a non-ASCII character is the cause.

- [ ] **Step 10: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md docs/Data_Schema.md apps/web/src/content/help/en/ai-model-settings.md
git commit -m "chore: release 0.239.0"
```

- [ ] **Step 11: Push and open the PR**

`main` is branch-protected. Never merge locally.

```bash
git push -u origin feat/chat-model-picker-redesign
```

Then `gh pr create`, with a body that states the deployment surface explicitly:

```
**Deployment surface:** server redeploy only (web + API). No desktop release - nothing under
`apps/desktop/src/**`, `apps/desktop/build/**` or `electron-builder.config.js` changed.
```

Do not add a `Fixes #n` line: there is no issue, because this is a feature rather than a bug fix.

---

## Self-review notes

**Spec coverage** - each of the handoff's six changes maps to a task: titled header (7), capability icons (3+4+7), binary-K context (6+7), description (1+2+3+4+5+7), 372px width (7), footer legend (7). Its "API change required" section maps to tasks 1-5. Its scroll requirement is Task 7 Step 7 and its test at Step 2.

**Deliberate deviations**, each documented at the point of change: the name's `max-w-[55%]` cap (Task 7 Step 7 comment), arrow keys left alone (Design decisions §3), the briefcase's near-universal appearance accepted rather than designed around (§1).

**Type consistency** - `formatContext` is defined in Task 6 and consumed in Task 7 under that exact name. `ChatModelOption` gains `supportsTools` / `description` in Task 7 Step 1 matching the C# `SupportsTools` / `Description` from Tasks 3-4. `Resolved(model, platform)` is defined and used only within Task 3. `llmModelsDescription*` keys are defined in Task 5 Step 6 and used in Step 5.

**Known test edit outside the new work:** Task 7 Step 8 changes an assertion in the pre-existing viewport-clamp test, because widening the menu genuinely changes what that test can guarantee. The reason is recorded in the test itself rather than the assertion being quietly deleted.
