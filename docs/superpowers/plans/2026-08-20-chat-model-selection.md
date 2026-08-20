# Chat Model Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a chat user choose which of several administrator-approved models answers them, mid-conversation, with readable model names and a bulk import of every model on an endpoint.

**Architecture:** The `Chat` column of the routing grid keeps its single dot (still the default that serves new conversations, chat titles, and API callers). A new `ChatEnabled` flag on `LlmModel` marks additional models the picker offers. A single new service, `ChatModelCatalog`, owns the one rule "is this model offered for chat" so that the settings resolver, the context-window resolver and the picker endpoint cannot drift apart. Chat is already stateless per turn, so switching model mid-conversation needs only a `ModelId` on the stream request.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core + Postgres; React 19 + TS + Vite + Tailwind v4; xUnit (unit + Testcontainers integration); vitest + @testing-library/react.

**Design spec:** [`docs/superpowers/specs/2026-08-20-chat-model-selection-design.md`](../specs/2026-08-20-chat-model-selection-design.md)

## Global Constraints

Every task's requirements implicitly include this section.

- **TDD is mandatory.** Write the failing test, run it, watch it fail for the *stated reason*, then implement. No production code without a preceding failing test.
- **Mutation-verify every test.** After a test goes green, break the implementation on purpose, re-run, confirm the test fails, restore. This repo's dominant defect class is tests that cannot fail. When restoring a mutated `.cs` file, **edit it back in place** - restoring from a copy preserves the old mtime and MSBuild skips the rebuild, so you keep testing the mutated binary.
- **No em or en dashes in user-facing text.** Plain hyphen `-` only, in UI strings, i18n catalogs (`apps/web/src/locales/**`), release notes and help articles. Code comments and internal docs are unaffected.
- **Never `git add -A` or `git add .`** in this repo - it sweeps hundreds of untracked scratch files into the commit. Stage explicit paths, always.
- **No jest-dom.** Zero of the 230+ web test files use its matchers. Use plain assertions (`expect(el.textContent).toContain(...)`, `expect(el.hasAttribute("disabled")).toBe(true)`). Do not install it or edit `src/test-setup.ts`.
- **`dotnet test --filter "Name=X"` does not work here** despite CLAUDE.md. Use `--filter "FullyQualifiedName~X"`.
- **Build `Diariz.slnx` before pushing.** Unit-only runs miss compile breaks in the integration project and in CodeQL. Controller constructor changes have a second construction site in `tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs`.
- **Do not add `.AsSplitQuery()`.** Split queries are the app-wide default since 0.228.4.
- **Never commit or push to `main`.** Work on `feat/chat-model-selection` (already created). Finish by pushing and opening a PR.
- **Web tests that `vi.mock("../lib/api")` list every method explicitly.** Adding a component that calls a new api method requires adding that method to each affected mock factory, or the test fails with "not a function".
- Versions: PR 1 ships **`0.231.0`**, PR 2 ships **`0.232.0`**. Bump `version.json` plus all four mirrors (`apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
- Deployment surface for both PRs: **server redeploy only**.

---

# File structure

## PR 1 (`0.231.0`)

| File | Responsibility |
|---|---|
| `src/Diariz.Domain/Entities/LlmModel.cs` | + `DisplayName`, `ChatEnabled`, computed `Label` |
| `src/Diariz.Domain/Entities/UserSettings.cs` | + `ChatModelId` |
| `src/Diariz.Domain/DiarizDbContext.cs` | Column config + the `SET NULL` FK |
| `src/Diariz.Domain/Migrations/*_ChatModelSelection.cs` | Generated migration |
| `src/Diariz.Api/Services/Llm/ChatModelCatalog.cs` | **New.** The single source of "which models may serve chat" |
| `src/Diariz.Api/Services/Llm/LlmSettingsResolver.cs` | Accepts a model override, delegating the offered-check to the catalog |
| `src/Diariz.Api/Services/ChatContextResolver.cs` | Same override, so the dial agrees with the truncation |
| `src/Diariz.Api/Controllers/ChatModelsController.cs` | **New.** `GET /api/chat/models` for any signed-in user |
| `src/Diariz.Api/Controllers/LlmModelsController.cs` | `DisplayName` on the upsert + the `chat-enabled` toggle |
| `src/Diariz.Api/Controllers/ChatController.cs` | Threads `req.ModelId` into both resolvers |
| `src/Diariz.Api/Controllers/UserSettingsController.cs` | Reads/writes the remembered pick |
| `src/Diariz.Api/Contracts/ApiDtos.cs` | DTO fields |
| `apps/web/src/components/ChatModelPicker.tsx` | **New.** Sparkle button + dropdown |
| `apps/web/src/components/ChatPanel.tsx` | Picker wiring, dial, `modelId` on the wire, save/restore |
| `apps/web/src/components/llmmodels/RoutingMatrix.tsx` | Label + the `In chat` column |
| `apps/web/src/components/llmmodels/ModelEditorDrawer.tsx` | Display-name field |
| `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts` | Client methods and types |

## PR 2 (`0.232.0`)

| File | Responsibility |
|---|---|
| `src/Diariz.Api/Services/Llm/LlmModelDiscovery.cs` | **New.** Pure parse + filter, no HTTP |
| `src/Diariz.Api/Services/Llm/LlmModelDiscoveryClient.cs` | **New.** The bounded outbound fetch |
| `src/Diariz.Api/Controllers/LlmModelsController.cs` | `discover` + `discover/import` |
| `apps/web/src/components/llmmodels/DiscoverModelsDialog.tsx` | **New.** The Add-all dialog |

---

# PR 1 - labels, the in-chat set, and the picker

### Task 1: Schema and the `Label` fallback

**Files:**
- Modify: `src/Diariz.Domain/Entities/LlmModel.cs`
- Modify: `src/Diariz.Domain/Entities/UserSettings.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs:657-664` (the `LlmModel` block) and `:624-652` (the `UserSettings` block)
- Create: `src/Diariz.Domain/Migrations/<timestamp>_ChatModelSelection.cs` (generated)
- Test: `tests/Diariz.Api.Tests/Llm/LlmModelLabelTests.cs` (create)
- Test: `tests/Diariz.Api.IntegrationTests/ChatModelSelectionSchemaTests.cs` (create)

**Interfaces:**
- Produces: `LlmModel.DisplayName` (`string?`), `LlmModel.ChatEnabled` (`bool`), `LlmModel.Label` (`string`, computed), `UserSettings.ChatModelId` (`Guid?`).

- [ ] **Step 1: Write the failing label test**

Create `tests/Diariz.Api.Tests/Llm/LlmModelLabelTests.cs`:

```csharp
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests.Llm;

/// <summary>The display name is what a user picking a model reads; the slug is what the endpoint needs.
/// Blank must fall back rather than render an empty picker row.</summary>
public class LlmModelLabelTests
{
    [Fact]
    public void Falls_back_to_the_slug_when_no_display_name_is_set()
    {
        var model = new LlmModel { Name = "qwen3.8-27b@q4_k_xl" };
        Assert.Equal("qwen3.8-27b@q4_k_xl", model.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_to_the_slug_for_a_blank_display_name(string blank)
    {
        var model = new LlmModel { Name = "qwen3.8-27b@q4_k_xl", DisplayName = blank };
        Assert.Equal("qwen3.8-27b@q4_k_xl", model.Label);
    }

    [Fact]
    public void Prefers_the_display_name_when_one_is_set()
    {
        var model = new LlmModel { Name = "qwen3.8-27b@q4_k_xl", DisplayName = "QWEN 3.8" };
        Assert.Equal("QWEN 3.8", model.Label);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmModelLabelTests"
```

Expected: compile error - `LlmModel` has no `DisplayName` or `Label`.

- [ ] **Step 3: Add the entity properties**

In `src/Diariz.Domain/Entities/LlmModel.cs`, after `Name`:

```csharp
    /// <summary>A user-facing name for this model, e.g. "QWEN 3.8". Null or blank means "use the slug" -
    /// see <see cref="Label"/>. Nullable rather than defaulted to the slug so that renaming the model
    /// still moves the label, instead of stranding a copy of the old slug.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Whether this model is offered in the chat model picker. The model assigned to
    /// <c>LlmCallGroup.Chat</c> is offered whether or not this is set - see ChatModelCatalog - so an
    /// administrator cannot produce an empty picker, or one that excludes the model actually in use.</summary>
    public bool ChatEnabled { get; set; }

    /// <summary>What a human reads. Never stored: deriving it means a slug rename cannot leave a stale
    /// label behind.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
```

In `src/Diariz.Domain/Entities/UserSettings.cs`, alongside the other nullable preferences:

```csharp
    /// <summary>The model this user last chose in the chat picker, or null to follow the platform's chat
    /// routing. FK to LlmModels with ON DELETE SET NULL: deleting a model must not be blocked because one
    /// user once picked it, and those users simply fall back to the default.
    ///
    /// Un-ticking a model's ChatEnabled does NOT clear this. The value is left pointing at the model and
    /// ignored while it is not offered, so re-ticking restores everyone's pick.</summary>
    public Guid? ChatModelId { get; set; }
```

- [ ] **Step 4: Run the label test and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmModelLabelTests"
```

Expected: 4 passed.

- [ ] **Step 5: Mutation-verify**

Change `Label` to `=> Name;`, re-run, confirm `Prefers_the_display_name_when_one_is_set` fails. Edit it back in place.

- [ ] **Step 6: Configure the columns and the FK**

In `DiarizDbContext.OnModelCreating`, inside the existing `builder.Entity<LlmModel>` block:

```csharp
            e.Property(m => m.DisplayName).HasMaxLength(128);
            e.Property(m => m.ChatEnabled).HasDefaultValue(false);
            e.Ignore(m => m.Label);   // computed in C#; there is no column behind it
```

Inside the existing `builder.Entity<UserSettings>` block, after the `HasOne(s => s.User)` relationship:

```csharp
            // SetNull, unlike the Restrict on LlmCallAssignment and PlatformSettings.DefaultLlmModelId.
            // Those two express "this model is in use by the platform, refuse the delete". A user's pick is
            // a preference, and blocking an administrator's delete because one user chose that model once
            // would make models effectively undeletable. LlmModelsController.Delete must therefore NOT grow
            // a check for this column.
            e.HasOne<LlmModel>()
                .WithMany()
                .HasForeignKey(s => s.ChatModelId)
                .OnDelete(DeleteBehavior.SetNull);
```

- [ ] **Step 7: Generate the migration**

```bash
dotnet ef migrations add ChatModelSelection --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Open the generated `Up` and confirm it contains exactly three `AddColumn` calls (`DisplayName`, `ChatEnabled` with `defaultValue: false`, `ChatModelId`), one `CreateIndex` on `UserSettings.ChatModelId`, and one `AddForeignKey` with `onDelete: ReferentialAction.SetNull`. **There must be no data-modifying SQL** - the chat default is offered implicitly, so no backfill is needed, and a backfill here would be a one-way data move for no behavioural gain.

Add this comment at the top of the generated migration class:

```csharp
/// <summary>Additive only, so an older backup restores its columns and this adds these on top -
/// MaintenanceController.CurrentFormat is deliberately NOT bumped.
///
/// No backfill of ChatEnabled: ChatModelCatalog offers the chat-assigned model implicitly, so a platform
/// upgraded with zero ChatEnabled rows behaves exactly as it did before.</summary>
```

- [ ] **Step 8: Write the failing integration test**

Create `tests/Diariz.Api.IntegrationTests/ChatModelSelectionSchemaTests.cs`:

```csharp
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The SET NULL cascade on a user's chat-model pick. The in-memory provider enforces no FKs at
/// all, so a unit test here would pass whatever the delete behaviour actually was.</summary>
[Collection(IntegrationCollection.Name)]
public class ChatModelSelectionSchemaTests(ContainersFixture fx)
{
    [Fact]
    public async Task Deleting_a_model_a_user_picked_succeeds_and_clears_the_pick()
    {
        await using var db = fx.CreateDbContext();

        var user = await Users.EnsureAsync(db);
        var model = new LlmModel
        {
            Id = Guid.NewGuid(), Name = $"m-{Guid.NewGuid():N}", ApiBase = "http://llm.test/v1",
            ContextLength = 8192, ChatEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmModels.Add(model);
        db.UserSettings.Add(new UserSettings { UserId = user.Id, ChatModelId = model.Id });
        await db.SaveChangesAsync();

        // Untracked delete, so the database's own rule is what decides - not EF's change tracker.
        await using (var fresh = fx.CreateDbContext())
        {
            fresh.LlmModels.Remove(await fresh.LlmModels.SingleAsync(m => m.Id == model.Id));
            await fresh.SaveChangesAsync();
        }

        await using var check = fx.CreateDbContext();
        var settings = await check.UserSettings.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.Null(settings.ChatModelId);
    }
}
```

If `Users.EnsureAsync` does not exist in the integration project, use whatever seeding helper the sibling tests in that directory use for a real user row - check `tests/Diariz.Api.IntegrationTests/` for the established pattern and follow it rather than inventing one.

- [ ] **Step 9: Run the integration test (needs Docker)**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~ChatModelSelectionSchemaTests"
```

Expected: PASS. If it fails with a foreign-key violation, the migration's `onDelete` is wrong - fix the `OnDelete(DeleteBehavior.SetNull)` and regenerate.

- [ ] **Step 10: Mutation-verify**

Change the `OnDelete` to `DeleteBehavior.Restrict`, regenerate the migration, re-run: the test must fail with an FK violation. Restore `SetNull` and regenerate.

- [ ] **Step 11: Commit**

```bash
git add src/Diariz.Domain/Entities/LlmModel.cs src/Diariz.Domain/Entities/UserSettings.cs src/Diariz.Domain/DiarizDbContext.cs src/Diariz.Domain/Migrations tests/Diariz.Api.Tests/Llm/LlmModelLabelTests.cs tests/Diariz.Api.IntegrationTests/ChatModelSelectionSchemaTests.cs
git commit -m "feat: add model display names and a chat-offered flag"
```

---

### Task 2: `ChatModelCatalog` - the one rule for what chat may use

**Files:**
- Create: `src/Diariz.Api/Services/Llm/ChatModelCatalog.cs`
- Modify: `src/Diariz.Api/Program.cs` (DI registration, next to the other `ILlmSettingsResolver`-era services)
- Test: `tests/Diariz.Api.Tests/Llm/ChatModelCatalogTests.cs` (create)

**Interfaces:**
- Consumes: `LlmModel.Label`, `LlmModel.ChatEnabled` (Task 1).
- Produces:
  - `record ChatModelOption(Guid Id, string Label, string Name, int ContextLength, bool IsDefault)`
  - `interface IChatModelCatalog`
    - `Task<Guid?> DefaultModelIdAsync(CancellationToken ct = default)`
    - `Task<IReadOnlyList<ChatModelOption>> ListAsync(CancellationToken ct = default)`
    - `Task<Guid?> ResolveOfferedAsync(Guid? requested, CancellationToken ct = default)`

> **Why this file exists.** The rule "may this model serve chat" is needed in three places: the settings resolver, the context-window resolver, and the picker endpoint. Written three times it would agree by luck and diverge on the first change - the picker would offer a model the resolver then refused, or worse, the reverse. One implementation, three callers.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/Llm/ChatModelCatalogTests.cs`:

```csharp
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

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
        // picker that excludes it could show no current selection at all.
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model", chatEnabled: false);
        AssignChat(db, chat.Id);

        var options = await new ChatModelCatalog(db).ListAsync();

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

        var options = await new ChatModelCatalog(db).ListAsync();

        Assert.Equal(["chat-model", "Alpha", "zeta"], options.Select(o => o.Label));
        Assert.Equal([true, false, false], options.Select(o => o.IsDefault));
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

        Assert.Equal(fallback.Id, await new ChatModelCatalog(db).DefaultModelIdAsync());
    }

    [Fact]
    public async Task Resolves_an_offered_model_to_itself()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model");
        var big = Seed(db, "big-model", chatEnabled: true);
        AssignChat(db, chat.Id);

        Assert.Equal(big.Id, await new ChatModelCatalog(db).ResolveOfferedAsync(big.Id));
    }

    [Fact]
    public async Task Ignores_a_model_that_is_not_offered()
    {
        // The security boundary: without this, any signed-in user could route their chat at any endpoint
        // the platform has configured, by posting an id they are not meant to have.
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model");
        var secret = Seed(db, "expensive-cloud-model", chatEnabled: false);
        AssignChat(db, chat.Id);

        Assert.Null(await new ChatModelCatalog(db).ResolveOfferedAsync(secret.Id));
    }

    [Fact]
    public async Task Ignores_an_unknown_id()
    {
        using var db = TestDb.Create();
        Assert.Null(await new ChatModelCatalog(db).ResolveOfferedAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Resolves_the_chat_default_even_though_its_flag_is_off()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model", chatEnabled: false);
        AssignChat(db, chat.Id);

        Assert.Equal(chat.Id, await new ChatModelCatalog(db).ResolveOfferedAsync(chat.Id));
    }

    [Fact]
    public async Task Resolves_null_for_no_request()
    {
        using var db = TestDb.Create();
        Assert.Null(await new ChatModelCatalog(db).ResolveOfferedAsync(null));
    }
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatModelCatalogTests"
```

Expected: compile error - `ChatModelCatalog` does not exist.

- [ ] **Step 3: Implement the catalog**

Create `src/Diariz.Api/Services/Llm/ChatModelCatalog.cs`:

```csharp
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services.Llm;

/// <summary>One model a chat user may pick. Carries no endpoint and no key: this is the shape returned to
/// every signed-in user, and the admin listing exists separately precisely because it exposes those.</summary>
public sealed record ChatModelOption(Guid Id, string Label, string Name, int ContextLength, bool IsDefault);

public interface IChatModelCatalog
{
    /// <summary>The model that serves chat when the user has chosen nothing: the Chat group's assignment,
    /// else the platform default, else null meaning the environment fallback.</summary>
    Task<Guid?> DefaultModelIdAsync(CancellationToken ct = default);

    /// <summary>What the picker offers: the default first, then every ChatEnabled model by label.</summary>
    Task<IReadOnlyList<ChatModelOption>> ListAsync(CancellationToken ct = default);

    /// <summary>The model id that should actually serve a turn, given what the caller asked for: the
    /// request's choice when it is offered, otherwise null meaning "fall through to normal routing".</summary>
    Task<Guid?> ResolveOfferedAsync(Guid? requested, CancellationToken ct = default);
}

/// <summary>The single authority on which models chat may use.
///
/// It exists as its own service because three callers need the same answer - the settings resolver (which
/// endpoint to call), the context resolver (which window to report), and the picker endpoint (what to
/// offer). Written separately in each, they would agree by coincidence and diverge on the first change,
/// producing a picker that offers a model the resolver then silently refuses.
///
/// <b>The chat-assigned model is offered whether or not its flag is set.</b> It is the model actually in
/// use, so excluding it would leave the picker unable to show the current selection.</summary>
public sealed class ChatModelCatalog(DiarizDbContext db) : IChatModelCatalog
{
    public async Task<Guid?> DefaultModelIdAsync(CancellationToken ct = default)
    {
        var assigned = await db.LlmCallAssignments
            .Where(a => a.Group == LlmCallGroup.Chat)
            .Select(a => (Guid?)a.LlmModelId)
            .FirstOrDefaultAsync(ct);

        return assigned ?? await db.PlatformSettings
            .Where(p => p.Id == PlatformSettings.SingletonId)
            .Select(p => p.DefaultLlmModelId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ChatModelOption>> ListAsync(CancellationToken ct = default)
    {
        var defaultId = await DefaultModelIdAsync(ct);

        var models = await db.LlmModels
            .Where(m => m.ChatEnabled || (defaultId != null && m.Id == defaultId))
            .AsNoTracking()
            .ToListAsync(ct);

        // Ordered in memory rather than in SQL: Label is a C# computed property, so it has no column to
        // sort on. The set is at most a handful of rows.
        return models
            .Select(m => new ChatModelOption(m.Id, m.Label, m.Name, m.ContextLength, m.Id == defaultId))
            .OrderByDescending(o => o.IsDefault)
            .ThenBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<Guid?> ResolveOfferedAsync(Guid? requested, CancellationToken ct = default)
    {
        if (requested is not { } id) return null;

        if (id == await DefaultModelIdAsync(ct)) return id;

        return await db.LlmModels.AnyAsync(m => m.Id == id && m.ChatEnabled, ct) ? id : null;
    }
}
```

- [ ] **Step 4: Register it in DI**

In `src/Diariz.Api/Program.cs`, next to the existing `ILlmSettingsResolver` registration:

```csharp
builder.Services.AddScoped<IChatModelCatalog, ChatModelCatalog>();
```

- [ ] **Step 5: Run and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatModelCatalogTests"
```

Expected: 8 passed.

- [ ] **Step 6: Mutation-verify the security boundary**

In `ResolveOfferedAsync`, change the final line to `return id;`. Re-run: `Ignores_a_model_that_is_not_offered` and `Ignores_an_unknown_id` must both fail. Restore in place.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Services/Llm/ChatModelCatalog.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/Llm/ChatModelCatalogTests.cs
git commit -m "feat: add ChatModelCatalog, the single rule for chat-offered models"
```

---

### Task 3: Model override in both resolvers

**Files:**
- Modify: `src/Diariz.Api/Services/Llm/LlmSettingsResolver.cs`
- Modify: `src/Diariz.Api/Services/ChatContextResolver.cs`
- Modify: `tests/Diariz.Api.TestSupport/Fakes.cs:157-176` (`FakeLlmSettingsResolver`)
- Test: `tests/Diariz.Api.Tests/Llm/LlmSettingsResolverTests.cs` (append)
- Test: `tests/Diariz.Api.Tests/ChatContextResolverTests.cs` (append)

**Interfaces:**
- Consumes: `IChatModelCatalog.ResolveOfferedAsync` (Task 2).
- Produces:
  - `ILlmSettingsResolver.ResolveAsync(LlmCallKind kind, Guid? modelOverride, CancellationToken ct = default)` - the new primary; the old 2-arg form becomes a default interface method delegating with `null`.
  - `IChatContextResolver.ResolveContextWindowAsync(Guid? modelOverride, CancellationToken ct = default)` - same shape.
  - `FakeLlmSettingsResolver.LastModelOverride` (`Guid?`).

- [ ] **Step 1: Write the failing resolver tests**

Append to `tests/Diariz.Api.Tests/Llm/LlmSettingsResolverTests.cs` (the file already has a `Seed` helper - reuse it, adding `chatEnabled` if its signature does not take one):

```csharp
    [Fact]
    public async Task Honours_a_chat_model_override_the_administrator_offers()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model", "http://chat/v1");
        var big = Seed(db, "big-model", "http://big/v1");
        big.ChatEnabled = true;
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        var cfg = await Build(db).ResolveAsync(LlmCallKind.ChatMessage, big.Id);

        Assert.Equal("big-model", cfg.Model);
        Assert.Equal("http://big/v1", cfg.ApiBase);
    }

    [Fact]
    public async Task Ignores_an_override_for_a_model_that_is_not_offered_for_chat()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model", "http://chat/v1");
        var secret = Seed(db, "expensive-model", "http://secret/v1");   // ChatEnabled stays false
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        var cfg = await Build(db).ResolveAsync(LlmCallKind.ChatMessage, secret.Id);

        Assert.Equal("chat-model", cfg.Model);
    }

    [Fact]
    public async Task Ignores_an_override_for_a_chat_title()
    {
        // Titling is background housekeeping the user never sees; it stays on the chat default so a slow
        // or expensive picked model is never used for a one-line title.
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model", "http://chat/v1");
        var big = Seed(db, "big-model", "http://big/v1");
        big.ChatEnabled = true;
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        var cfg = await Build(db).ResolveAsync(LlmCallKind.ChatTitle, big.Id);

        Assert.Equal("chat-model", cfg.Model);
    }

    [Fact]
    public async Task Ignores_an_override_for_a_non_chat_call_kind()
    {
        using var db = TestDb.Create();
        var summaries = Seed(db, "summary-model", "http://sum/v1");
        var big = Seed(db, "big-model", "http://big/v1");
        big.ChatEnabled = true;
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Summaries, LlmModelId = summaries.Id });
        await db.SaveChangesAsync();

        var cfg = await Build(db).ResolveAsync(LlmCallKind.Summarize, big.Id);

        Assert.Equal("summary-model", cfg.Model);
    }

    [Fact]
    public async Task Sizes_the_context_budget_from_the_overridden_model()
    {
        // The budget is what actually truncates transcript text. If it kept using the default model's
        // window, picking a 200k model would silently still cut context at the small model's size.
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model", "http://chat/v1");
        chat.ContextLength = 8_192;
        var big = Seed(db, "big-model", "http://big/v1");
        big.ContextLength = 200_000;
        big.ChatEnabled = true;
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        var cfg = await Build(db).ResolveAsync(LlmCallKind.ChatMessage, big.Id);

        Assert.Equal(LlmContextBudget.CharsFor(200_000), cfg.ContextCharBudget);
    }
```

If the file's `Seed` helper does not set `ChatEnabled`, mutate the returned entity as shown above and `SaveChangesAsync`. Add `using Diariz.Api.Services;` for `LlmContextBudget` if it is not already imported.

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmSettingsResolverTests"
```

Expected: compile error - no 2-argument `ResolveAsync(kind, Guid)` overload.

- [ ] **Step 3: Change the interface**

In `src/Diariz.Api/Services/Llm/LlmSettingsResolver.cs`, replace the `ILlmSettingsResolver` interface body:

```csharp
public interface ILlmSettingsResolver
{
    /// <summary>Resolves the model and parameters for a call of this kind.
    ///
    /// The kind is a parameter rather than being read from the ambient <see cref="LlmCallScope"/>, even
    /// though the scope already carries it. Ambient is fine for telemetry, where a missing scope logs
    /// <c>Unknown</c> and is visible and fixable; it is not fine for behaviour, where a missing scope would
    /// silently apply the wrong model. Every caller already knows its kind - it pushes the scope with it.
    ///
    /// <paramref name="modelOverride"/> is a model the END USER chose. It is honoured only for the Chat
    /// group and only for a model an administrator offers - <see cref="IChatModelCatalog"/> decides, and
    /// that check is the security boundary. It lives here rather than in the controller so that no future
    /// caller can skip it.</summary>
    Task<LlmRequestConfig> ResolveAsync(
        LlmCallKind kind, Guid? modelOverride, CancellationToken ct = default);

    /// <summary>No user-chosen model: every call site except a chat turn.</summary>
    Task<LlmRequestConfig> ResolveAsync(LlmCallKind kind, CancellationToken ct = default) =>
        ResolveAsync(kind, null, ct);
}
```

- [ ] **Step 4: Implement the override in the resolver**

In `LlmSettingsResolver`, take the catalog in the constructor and thread the override through:

```csharp
    private readonly IChatModelCatalog _chatModels;
```

Add `IChatModelCatalog chatModels` as a constructor parameter (before the optional `chat` parameter, so the existing optional argument stays last) and assign it.

Change the method signature and the one call it makes:

```csharp
    public async Task<LlmRequestConfig> ResolveAsync(
        LlmCallKind kind, Guid? modelOverride, CancellationToken ct = default)
    {
        var group = LlmCallGroups.GroupFor(kind);
        var ps = await _db.PlatformSettings
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId, ct);

        // A user-chosen model applies to a chat MESSAGE only. ChatTitle shares the Chat group but is
        // background housekeeping, so it stays on whatever the administrator routed.
        var chosen = kind == LlmCallKind.ChatMessage
            ? await _chatModels.ResolveOfferedAsync(modelOverride, ct)
            : null;

        var model = await ChooseModelAsync(group, chosen, ps, ct);
        // ... rest unchanged
```

And in `ChooseModelAsync`, add the parameter and prefer it:

```csharp
    private async Task<LlmModel?> ChooseModelAsync(
        LlmCallGroup? group, Guid? chosen, PlatformSettings? ps, CancellationToken ct)
    {
        // Already validated as offered by ChatModelCatalog, so it outranks the routing table.
        Guid? id = chosen;

        if (id is null && group is not null)
            id = await _db.LlmCallAssignments
                .Where(a => a.Group == group.Value)
                .Select(a => (Guid?)a.LlmModelId)
                .FirstOrDefaultAsync(ct);

        id ??= ps?.DefaultLlmModelId;
        if (id is null) return null;

        return await _db.LlmModels
            .Include(m => m.Parameters)
            .FirstOrDefaultAsync(m => m.Id == id.Value, ct);
    }
```

- [ ] **Step 5: Update the test harness `Build` and the fake**

In `tests/Diariz.Api.Tests/Llm/LlmSettingsResolverTests.cs`, add `new ChatModelCatalog(db)` to the `Build` helper's constructor arguments in the right position.

In `tests/Diariz.Api.TestSupport/Fakes.cs`, change `FakeLlmSettingsResolver` to implement the 3-argument method and record the override:

```csharp
    /// <summary>The model override of the last call. Chat is the only caller that sends one, so this is
    /// how a test proves the request's ModelId actually reached the resolver.</summary>
    public Guid? LastModelOverride { get; private set; }

    public Task<LlmRequestConfig> ResolveAsync(
        LlmCallKind kind, Guid? modelOverride, CancellationToken ct = default)
    {
        LastKind = kind;
        LastModelOverride = modelOverride;
        return Task.FromResult(Config);
    }
```

Delete the old 2-argument implementation - the default interface method supplies it.

- [ ] **Step 6: Run and watch the resolver tests pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmSettingsResolverTests"
```

Expected: all pass, including the five new ones.

- [ ] **Step 7: Write the failing context-resolver tests**

Append to `tests/Diariz.Api.Tests/ChatContextResolverTests.cs`:

```csharp
    [Fact]
    public async Task Reports_the_window_of_an_offered_override()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, 8_192);
        var big = Seed(db, 200_000);
        big.ChatEnabled = true;
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        Assert.Equal(200_000, await Build(db).ResolveContextWindowAsync(big.Id));
    }

    [Fact]
    public async Task Ignores_an_override_that_is_not_offered()
    {
        // The dial must report the window that will actually be used. If it followed a model the resolver
        // then refused, the gauge and the real truncation would disagree - the exact defect this resolver
        // was written to prevent.
        using var db = TestDb.Create();
        var chat = Seed(db, 8_192);
        var secret = Seed(db, 200_000);   // ChatEnabled stays false
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        Assert.Equal(8_192, await Build(db).ResolveContextWindowAsync(secret.Id));
    }
```

The existing `Seed` helper returns the model, so `big.ChatEnabled = true` then `SaveChangesAsync` works as written.

- [ ] **Step 8: Run and watch it fail, then implement**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatContextResolverTests"
```

Expected: compile error. Then in `src/Diariz.Api/Services/ChatContextResolver.cs`:

```csharp
public interface IChatContextResolver
{
    /// <summary>Effective context-window size (tokens) for a chat turn: the context length of the model
    /// that actually serves the turn, else the server default. <paramref name="modelOverride"/> is the
    /// user's picked model, honoured on exactly the same terms as in
    /// <see cref="Llm.ILlmSettingsResolver"/> - both defer to <see cref="Llm.IChatModelCatalog"/>, so the
    /// dial cannot report a window the request will not use.</summary>
    Task<int> ResolveContextWindowAsync(Guid? modelOverride, CancellationToken ct = default);

    Task<int> ResolveContextWindowAsync(CancellationToken ct = default) =>
        ResolveContextWindowAsync(null, ct);
}
```

Take `IChatModelCatalog` in the constructor, and replace the body:

```csharp
    public async Task<int> ResolveContextWindowAsync(Guid? modelOverride, CancellationToken ct = default)
    {
        var id = await _chatModels.ResolveOfferedAsync(modelOverride, ct)
                 ?? await _chatModels.DefaultModelIdAsync(ct);

        if (id is { } modelId)
        {
            var length = await _db.LlmModels
                .Where(m => m.Id == modelId)
                .Select(m => m.ContextLength)
                .FirstOrDefaultAsync(ct);

            if (length > 0) return length;
        }

        return _opts.ContextLength;
    }
```

Fix every `new ChatContextResolver(db, Options.Create(...))` construction site the compiler flags (at minimum `ChatContextResolverTests.Build` and `ChatControllerTests.Build`) by adding `new ChatModelCatalog(db)`.

- [ ] **Step 9: Run both resolver suites and the whole unit project**

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: green, no warnings.

- [ ] **Step 10: Mutation-verify**

In `LlmSettingsResolver.ResolveAsync`, change the chosen-model line to `var chosen = await _chatModels.ResolveOfferedAsync(modelOverride, ct);` (dropping the `ChatMessage` guard). Re-run: `Ignores_an_override_for_a_chat_title` must fail. Restore in place.

- [ ] **Step 11: Commit**

```bash
git add src/Diariz.Api/Services/Llm/LlmSettingsResolver.cs src/Diariz.Api/Services/ChatContextResolver.cs tests/Diariz.Api.TestSupport/Fakes.cs tests/Diariz.Api.Tests/Llm/LlmSettingsResolverTests.cs tests/Diariz.Api.Tests/ChatContextResolverTests.cs
git commit -m "feat: honour a user-chosen chat model in both resolvers"
```

---

### Task 4: `GET /api/chat/models`

**Files:**
- Create: `src/Diariz.Api/Controllers/ChatModelsController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Test: `tests/Diariz.Api.Tests/ChatModelsControllerTests.cs` (create)

**Interfaces:**
- Consumes: `IChatModelCatalog.ListAsync` (Task 2).
- Produces: `record ChatModelDto(Guid Id, string Label, string Name, int ContextLength, bool IsDefault)`; route `GET /api/chat/models`.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/ChatModelsControllerTests.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The picker's source. Every signed-in user reads this, so it must expose no endpoint and no key -
/// which is exactly why it exists instead of reusing the admin listing.</summary>
public class ChatModelsControllerTests
{
    private static ChatModelsController Build(DiarizDbContext db) =>
        new(new ChatModelCatalog(db)) { ControllerContext = Http.Context(Guid.NewGuid()) };

    private static LlmModel Seed(DiarizDbContext db, string name, bool chatEnabled, string? displayName = null)
    {
        var m = new LlmModel
        {
            Id = Guid.NewGuid(), Name = name, DisplayName = displayName,
            ApiBase = "http://secret-internal-host:1234/v1", ApiKeyEncrypted = "enc:super-secret",
            ContextLength = 32_768, ChatEnabled = chatEnabled,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmModels.Add(m);
        db.SaveChanges();
        return m;
    }

    [Fact]
    public async Task Never_exposes_the_endpoint_or_the_key()
    {
        using var db = TestDb.Create();
        Seed(db, "m", chatEnabled: true);

        var result = await Build(db).List();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);

        Assert.DoesNotContain("secret-internal-host", json);
        Assert.DoesNotContain("super-secret", json);
    }

    [Fact]
    public async Task Returns_the_label_and_the_slug()
    {
        using var db = TestDb.Create();
        Seed(db, "qwen3.8-27b@q4_k_xl", chatEnabled: true, displayName: "QWEN 3.8");

        var result = await Build(db).List();
        var dto = Assert.Single(Assert.IsType<List<ChatModelDto>>(result.Value));

        Assert.Equal("QWEN 3.8", dto.Label);
        Assert.Equal("qwen3.8-27b@q4_k_xl", dto.Name);
        Assert.Equal(32_768, dto.ContextLength);
    }

    [Fact]
    public async Task Omits_a_model_that_is_not_offered()
    {
        using var db = TestDb.Create();
        Seed(db, "offered", chatEnabled: true);
        Seed(db, "not-offered", chatEnabled: false);

        var dtos = Assert.IsType<List<ChatModelDto>>((await Build(db).List()).Value);

        Assert.Equal(["offered"], dtos.Select(d => d.Name));
    }
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatModelsControllerTests"
```

Expected: compile error - no `ChatModelsController`.

- [ ] **Step 3: Add the DTO**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, near the other chat records:

```csharp
/// <summary>One model a chat user may pick. Deliberately carries no endpoint and no API key: every signed-in
/// user reads this, unlike <see cref="LlmModelDto"/> which is administrator-only for exactly that reason.
/// <paramref name="Name"/> is the slug the server sends as <c>model</c>, present so the client can match a
/// streamed usage snapshot back to a label.</summary>
public record ChatModelDto(Guid Id, string Label, string Name, int ContextLength, bool IsDefault);
```

- [ ] **Step 4: Add the controller**

Create `src/Diariz.Api/Controllers/ChatModelsController.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>The models a chat user may choose between.
///
/// Its own controller rather than an action on ChatController: the route is read by every signed-in user
/// while ChatController's actions are all per-user data operations, and keeping the surface that exposes
/// platform configuration small and separate makes the "no endpoint, no key" contract easy to see.</summary>
[ApiController]
[Route("api/chat/models")]
[Authorize]
public class ChatModelsController(IChatModelCatalog catalog) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("List the models you can chat with")]
    [EndpointDescription(
        "The models a Platform Administrator offers for chat: the default first, then the rest by name. " +
        "Pass an `id` from this list as `modelId` on a chat request to use that model for the turn; a model " +
        "not in this list is ignored and the default answers instead.\n\n" +
        "Endpoints and API keys are never returned - those are administrator-only.")]
    public async Task<ActionResult<List<ChatModelDto>>> List(CancellationToken ct = default)
    {
        var options = await catalog.ListAsync(ct);
        return options
            .Select(o => new ChatModelDto(o.Id, o.Label, o.Name, o.ContextLength, o.IsDefault))
            .ToList();
    }
}
```

- [ ] **Step 5: Run and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatModelsControllerTests"
```

Expected: 3 passed.

- [ ] **Step 6: Mutation-verify**

Temporarily widen the projection to the admin shape - add `ApiBase = "http://secret-internal-host:1234/v1"` to an anonymous object returned in place of the DTO. Re-run: `Never_exposes_the_endpoint_or_the_key` must fail. Restore in place. Then change the catalog call to `db.LlmModels.ToListAsync()` and confirm `Omits_a_model_that_is_not_offered` fails. Restore.

- [ ] **Step 7: Regenerate the OpenAPI snapshot**

The OpenAPI snapshot test rewrites its own file, so the first run fails and the second passes with no code change. Run it twice and commit the regenerated snapshot.

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
```

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Controllers/ChatModelsController.cs src/Diariz.Api/Contracts/ApiDtos.cs tests/Diariz.Api.Tests/ChatModelsControllerTests.cs
git add tests/Diariz.Api.Tests/Snapshots
git commit -m "feat: add GET /api/chat/models for the chat model picker"
```

---

### Task 5: `ModelId` on the chat stream request

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs:718-725` (`ChatStreamRequest`)
- Modify: `src/Diariz.Api/Controllers/ChatController.cs:116` and `:181`
- Test: `tests/Diariz.Api.Tests/ChatControllerTests.cs` (append)

**Interfaces:**
- Consumes: `ILlmSettingsResolver.ResolveAsync(kind, modelOverride, ct)` and `IChatContextResolver.ResolveContextWindowAsync(modelOverride, ct)` (Task 3); `FakeLlmSettingsResolver.LastModelOverride` (Task 3).
- Produces: `ChatStreamRequest.ModelId` (`Guid?`).

- [ ] **Step 1: Widen the test harness to expose the fake resolver**

`ChatControllerTests.Build` already constructs a `FakeLlmSettingsResolver` but discards it. Return it, so the assertion can read `LastModelOverride`. **Do not add a test-only member to `ChatController`** - this repo reaches internals through public seams and TestSupport fakes, never through `InternalsVisibleTo` or a `...ForTest` property.

```csharp
    private static (ChatController controller, DiarizDbContext db, FakeChatStreamClient chat,
        FakeLlmSettingsResolver settings) Build(
        Guid userId, bool llmEnabled = true, FakeAudioStorage? storage = null, FakeUrlFetcher? urlFetcher = null,
        FakeChatToolSettingsResolver? toolSettings = null, FakeChatStreamClient? chat = null)
    {
        // ... body unchanged ...
        return (controller, db, chat, settings);
    }
```

Every existing call site deconstructs three values positionally, so each needs a fourth element - add `, _` (or name it where the test needs it). The compiler lists them all.

- [ ] **Step 2: Write the failing test**

Append to `tests/Diariz.Api.Tests/ChatControllerTests.cs`:

```csharp
    [Fact]
    public async Task Passes_the_requested_model_to_the_settings_resolver()
    {
        // Proves the request's ModelId actually reaches the resolver. Whether to HONOUR it is the
        // resolver's decision - a controller-side check would be a second copy of that rule, and the two
        // would drift.
        var userId = Guid.NewGuid();
        var (controller, _, _, settings) = Build(userId);
        var chosen = Guid.NewGuid();

        await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "hello")], false, false, null, chosen),
            CancellationToken.None);

        Assert.Equal(chosen, settings.LastModelOverride);
    }

    [Fact]
    public async Task Sends_no_override_when_the_request_names_no_model()
    {
        var (controller, _, _, settings) = Build(Guid.NewGuid());

        await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "hello")]),
            CancellationToken.None);

        Assert.Null(settings.LastModelOverride);
    }
```

`ModelId` is added as a **trailing optional positional parameter** on `ChatStreamRequest`, consistent with `SectionId` above it - which is what makes both the 8-argument and the 4-argument calls above compile.

- [ ] **Step 3: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatControllerTests"
```

Expected: compile error - `ChatStreamRequest` has no such parameter.

- [ ] **Step 4: Add the field**

In `ApiDtos.cs`:

```csharp
public record ChatStreamRequest(
    IReadOnlyList<Guid> RecordingIds,
    string? AttachmentName,
    string? AttachmentText,
    IReadOnlyList<ChatTurnDto> Messages,
    bool IncludeAttachments = false,
    bool SearchAllMeetings = false,
    Guid? SectionId = null,
    /// <summary>A model from <c>GET /api/chat/models</c> to answer this turn. Null, unknown, or a model the
    /// platform does not offer for chat all mean the same thing: the administrator's chat model answers.
    /// The request is stateless, so switching model mid-conversation needs nothing else - the full history
    /// is resent anyway and goes to the new model as a matter of course.</summary>
    Guid? ModelId = null);
```

Extend the endpoint's `[EndpointDescription]` with one sentence naming `modelId` and pointing at `GET /api/chat/models`.

- [ ] **Step 5: Thread it through the controller**

`src/Diariz.Api/Controllers/ChatController.cs` line 116:

```csharp
        var cfg = await _settings.ResolveAsync(LlmCallKind.ChatMessage, req.ModelId, ct);
```

Line 181:

```csharp
        var contextTotal = await _contextResolver.ResolveContextWindowAsync(req.ModelId, ct);
```

Leave the `ChatTitle` resolution at line 572 alone - it must stay on the chat default.

- [ ] **Step 6: Run and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatControllerTests"
```

- [ ] **Step 7: Mutation-verify**

Change line 116 back to `ResolveAsync(LlmCallKind.ChatMessage, ct)`. Re-run: the new test must fail with `Assert.Equal() Failure: null`. Restore in place.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/ChatController.cs tests/Diariz.Api.Tests/ChatControllerTests.cs
git commit -m "feat: accept a per-turn model on the chat stream request"
```

---

### Task 6: Admin API - display name and the chat-enabled toggle

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs:871-877`
- Modify: `src/Diariz.Api/Controllers/LlmModelsController.cs` (Create, Update, `ToDto`, + a new action)
- Test: `tests/Diariz.Api.Tests/LlmModelsControllerTests.cs` (append)

**Interfaces:**
- Produces: `LlmModelDto` gains `DisplayName` (`string?`) and `ChatEnabled` (`bool`); `LlmModelUpsert` gains `DisplayName` **only**; `PUT /api/admin/llm-models/{id}/chat-enabled` taking `SetChatEnabledRequest(bool Enabled)`.

> **`LlmModelUpsert` must not carry `ChatEnabled`.** The drawer does not edit it, so if it were on the upsert every drawer save would post whatever stale value the client held and silently reset the administrator's checkbox. The toggle owns that column alone.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Diariz.Api.Tests/LlmModelsControllerTests.cs`:

```csharp
    [Fact]
    public async Task Round_trips_a_display_name()
    {
        using var db = TestDb.Create();
        var created = await Build(db).Create(Upsert("m") with { DisplayName = "QWEN 3.8" });

        var dto = Assert.IsType<LlmModelDto>(created.Value);
        Assert.Equal("QWEN 3.8", dto.DisplayName);
    }

    [Fact]
    public async Task Toggling_chat_enabled_persists()
    {
        using var db = TestDb.Create();
        var model = Seed(db);

        var result = await Build(db).SetChatEnabled(model.Id, new SetChatEnabledRequest(true));

        Assert.IsType<NoContentResult>(result);
        Assert.True((await db.LlmModels.FindAsync(model.Id))!.ChatEnabled);
    }

    [Fact]
    public async Task Toggling_chat_enabled_on_a_missing_model_is_not_found()
    {
        using var db = TestDb.Create();
        Assert.IsType<NotFoundResult>(
            await Build(db).SetChatEnabled(Guid.NewGuid(), new SetChatEnabledRequest(true)));
    }

    [Fact]
    public async Task Saving_the_editor_does_not_reset_chat_enabled()
    {
        // The drawer never edits this flag, so an upsert must leave it alone. If ChatEnabled were part of
        // LlmModelUpsert, every save from the editor would post a stale value and silently un-offer the
        // model - a setting that quietly reverts is worse than one that cannot be changed.
        using var db = TestDb.Create();
        var model = Seed(db);
        await Build(db).SetChatEnabled(model.Id, new SetChatEnabledRequest(true));

        await Build(db).Update(model.Id, Upsert("m") with { DisplayName = "Renamed" });

        Assert.True((await db.LlmModels.FindAsync(model.Id))!.ChatEnabled);
    }
```

`Upsert(...) with { DisplayName = ... }` requires `DisplayName` to be a named member of the record - adding it as a trailing optional positional parameter gives exactly that.

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmModelsControllerTests"
```

Expected: compile errors on `DisplayName` and `SetChatEnabled`.

- [ ] **Step 3: Extend the DTOs**

```csharp
public record LlmModelDto(Guid Id, string Name, string ApiBase, bool HasApiKey, int ContextLength,
    Dictionary<string, string> Parameters, string? DisplayName = null, bool ChatEnabled = false);

/// <summary>Create or replace a model. A null <c>ApiKey</c> on update means "keep the stored key" - the UI
/// was never given it, so it cannot send it back.
///
/// <c>ChatEnabled</c> is deliberately absent: it has its own endpoint, so an editor save cannot post a
/// stale value and silently un-offer the model.</summary>
public record LlmModelUpsert(string Name, string ApiBase, string? ApiKey, int ContextLength,
    Dictionary<string, string> Parameters, string? DisplayName = null);

/// <summary>Whether a model appears in the chat model picker.</summary>
public record SetChatEnabledRequest(bool Enabled);
```

- [ ] **Step 4: Wire it into the controller**

In `Create`, add `DisplayName = Trim(req.DisplayName),` to the object initialiser. In `Update`, add `model.DisplayName = Trim(req.DisplayName);`. Add the helper and the action:

```csharp
    /// <summary>Blank means "use the slug", so it is stored as null rather than as an empty string - one
    /// representation of absent, so Label has one thing to test.</summary>
    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Whether this model appears in the chat picker.
    ///
    /// Its own route rather than a field on the upsert: the editor drawer does not show this control, so an
    /// upsert carrying it would let a save from the drawer silently reset an administrator's choice.</summary>
    [HttpPut("{id:guid}/chat-enabled")]
    public async Task<IActionResult> SetChatEnabled(Guid id, SetChatEnabledRequest req)
    {
        var model = await _db.LlmModels.FirstOrDefaultAsync(m => m.Id == id);
        if (model is null) return NotFound();

        model.ChatEnabled = req.Enabled;
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        return NoContent();
    }
```

And extend `ToDto`:

```csharp
    private static LlmModelDto ToDto(LlmModel m) => new(
        m.Id, m.Name, m.ApiBase,
        HasApiKey: !string.IsNullOrEmpty(m.ApiKeyEncrypted),
        m.ContextLength,
        m.Parameters.ToDictionary(p => p.Group.ToString(), p => p.ParametersJson),
        m.DisplayName, m.ChatEnabled);
```

- [ ] **Step 5: Run and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmModelsControllerTests"
```

- [ ] **Step 6: Mutation-verify**

Add `model.ChatEnabled = false;` to `Update`. Re-run: `Saving_the_editor_does_not_reset_chat_enabled` must fail. Remove it in place.

- [ ] **Step 7: Regenerate the OpenAPI snapshot (twice) and commit**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/LlmModelsController.cs tests/Diariz.Api.Tests/LlmModelsControllerTests.cs tests/Diariz.Api.Tests/Snapshots
git commit -m "feat: expose model display names and a chat-enabled toggle"
```

---

### Task 7: Remembering the user's pick

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (`UserSettingsDto`, `UpdateUserSettingsRequest`)
- Modify: `src/Diariz.Api/Controllers/UserSettingsController.cs:60-66` and the `Update` body
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (`SavedChatContextDto`)
- Test: `tests/Diariz.Api.Tests/UserSettingsControllerTests.cs` (append; create if absent)

**Interfaces:**
- Consumes: `UserSettings.ChatModelId` (Task 1); the resolver overrides (Task 3).
- Produces: `UserSettingsDto.ChatModelId` (`Guid?`); `UpdateUserSettingsRequest.ChatModelId` (`Guid?`, where `Guid.Empty` clears); `SavedChatContextDto.ModelId` (`Guid?`).

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task Returns_the_remembered_chat_model()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = SeedChatModel(db);              // a ChatEnabled LlmModel
        db.UserSettings.Add(new UserSettings { UserId = userId, ChatModelId = model.Id });
        await db.SaveChangesAsync();

        var dto = await Build(db, userId).Get();

        Assert.Equal(model.Id, dto.ChatModelId);
    }

    [Fact]
    public async Task Reports_the_window_of_the_remembered_model_not_the_platform_default()
    {
        // This number seeds the chat dial before the first turn. If it reported the default model's window
        // the gauge would be wrong from the moment the panel opened until the user sent something.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var big = SeedChatModel(db, contextLength: 200_000);
        db.UserSettings.Add(new UserSettings { UserId = userId, ChatModelId = big.Id });
        await db.SaveChangesAsync();

        Assert.Equal(200_000, (await Build(db, userId).Get()).ContextWindow);
    }

    [Fact]
    public async Task Setting_the_chat_model_persists_it()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = SeedChatModel(db);

        await Build(db, userId).Update(new UpdateUserSettingsRequest { ChatModelId = model.Id });

        Assert.Equal(model.Id, (await db.UserSettings.FindAsync(userId))!.ChatModelId);
    }

    [Fact]
    public async Task An_empty_guid_clears_the_pick()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = SeedChatModel(db);
        db.UserSettings.Add(new UserSettings { UserId = userId, ChatModelId = model.Id });
        await db.SaveChangesAsync();

        await Build(db, userId).Update(new UpdateUserSettingsRequest { ChatModelId = Guid.Empty });

        Assert.Null((await db.UserSettings.FindAsync(userId))!.ChatModelId);
    }

    [Fact]
    public async Task Omitting_the_field_leaves_the_pick_alone()
    {
        // The settings tabs each PUT only their own fields, so an unrelated save must not wipe this.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = SeedChatModel(db);
        db.UserSettings.Add(new UserSettings { UserId = userId, ChatModelId = model.Id });
        await db.SaveChangesAsync();

        await Build(db, userId).Update(new UpdateUserSettingsRequest { ToolsEnabled = true });

        Assert.Equal(model.Id, (await db.UserSettings.FindAsync(userId))!.ChatModelId);
    }
```

Write `SeedChatModel` and `Build` following whatever harness `UserSettingsControllerTests` already uses; if that file does not exist, model the harness on `ChatControllerTests.Build` and construct the real `ChatContextResolver` and `LlmSettingsResolver` (both need `new ChatModelCatalog(db)`) rather than fakes, since the window value is the thing under test.

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserSettingsControllerTests"
```

- [ ] **Step 3: Extend the DTOs**

Add to `UserSettingsDto`, after `ChatModel`:

```csharp
    /// <summary>The model this user last chose in the chat picker, or null to follow the platform's chat
    /// routing. Unlike ContextWindow and ChatModel above it, this one IS writable.</summary>
    Guid? ChatModelId,
```

Because `UserSettingsDto` is positional with defaulted trailing parameters, place `ChatModelId` **before** the first defaulted parameter (`OutlookSyncEnabled`) or give it a default of its own - the compiler will tell you. Update the single construction site in `UserSettingsController.Get` accordingly.

Add to `UpdateUserSettingsRequest`:

```csharp
    /// <summary>The chat model picker's choice. Null leaves it unchanged; <c>Guid.Empty</c> clears the pick
    /// and follows the platform's chat routing; a value sets it. Empty-as-clear mirrors the "&lt;=0 clears"
    /// rule the numeric fields use - a separate boolean would be a second way to say the same thing.
    ///
    /// Not validated against the offered set here on purpose: an administrator can un-tick a model at any
    /// time, so a stored pick is always provisional. ChatModelCatalog ignores one that is not offered, which
    /// means an un-tick is reversible - clearing the column would destroy every user's choice permanently.</summary>
    Guid? ChatModelId = null,
```

Add to `SavedChatContextDto`:

```csharp
    /// <summary>The model this conversation was using when it was saved. Null for conversations saved
    /// before 0.231.0, and for one on the platform default.</summary>
    Guid? ModelId = null
```

- [ ] **Step 4: Wire the controller**

In `Get`, pass the pick to both resolvers:

```csharp
        var s = await _db.UserSettings.FindAsync(UserId);
        // ... existing ps / tools lookups
        return new UserSettingsDto(
            ContextWindow: await _contextResolver.ResolveContextWindowAsync(s?.ChatModelId),
            ChatModel: (await _llmSettings.ResolveAsync(LlmCallKind.ChatMessage, s?.ChatModelId)).Model,
            ChatModelId: s?.ChatModelId,
            // ... rest unchanged
```

In `Update`, alongside the other tri-state fields:

```csharp
        // Guid.Empty clears, a value sets, null leaves alone - the same three-way shape as the numeric
        // fields above.
        if (req.ChatModelId is { } chatModelId)
            s.ChatModelId = chatModelId == Guid.Empty ? null : chatModelId;
```

Extend the `[EndpointDescription]` on both actions with one sentence describing `chatModelId` and its empty-clears rule.

- [ ] **Step 5: Run, watch pass, then mutation-verify**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserSettingsControllerTests"
```

Then change the `Get` line back to `ResolveContextWindowAsync()` and confirm `Reports_the_window_of_the_remembered_model_not_the_platform_default` fails. Restore in place.

- [ ] **Step 6: Regenerate the OpenAPI snapshot (twice) and commit**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/UserSettingsController.cs tests/Diariz.Api.Tests/UserSettingsControllerTests.cs tests/Diariz.Api.Tests/Snapshots
git commit -m "feat: remember a user's chat model choice"
```

- [ ] **Step 7: Build the whole solution**

```bash
dotnet build Diariz.slnx
```

Expected: no errors, no warnings. A unit-only run would miss compile breaks in the integration project - in particular any `ChatController` or `ChatContextResolver` construction in `tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs`.

---

### Task 8: Web types and API client

**Files:**
- Modify: `apps/web/src/lib/types.ts:1484-1499` and `:508-514`
- Modify: `apps/web/src/lib/api.ts:1263-1290` (`chatStream`) and `:1666-1720` (models section)

**Interfaces:**
- Produces:
  - `interface ChatModelOption { id: string; label: string; name: string; contextLength: number; isDefault: boolean }`
  - `LlmModel` gains `displayName: string | null; chatEnabled: boolean`
  - `LlmModelUpsert` gains `displayName?: string | null`
  - `UserSettings` gains `chatModelId: string | null`; `UpdateUserSettings` gains `chatModelId?: string | null`
  - `api.listChatModels(): Promise<ChatModelOption[]>`
  - `api.setModelChatEnabled(id: string, enabled: boolean): Promise<void>`
  - `api.chatStream` body gains `modelId?: string | null`

- [ ] **Step 1: Add the types**

In `apps/web/src/lib/types.ts`:

```ts
/// One model the chat picker offers. Endpoint and key are absent by design - this comes from
/// /api/chat/models, which every signed-in user may read.
export interface ChatModelOption {
  id: string;
  /// What the user reads. Falls back to the slug server-side when no display name is set.
  label: string;
  /// The slug the server sends as `model`, so a streamed usage snapshot can be matched back to a label.
  name: string;
  contextLength: number;
  isDefault: boolean;
}
```

Extend `LlmModel` with `displayName: string | null;` and `chatEnabled: boolean;`, and `LlmModelUpsert` with `displayName?: string | null;` - **and nothing else**: `chatEnabled` deliberately has no place on the upsert, because the drawer does not edit it and a stale value would silently un-offer the model.

Extend `UserSettings` with:

```ts
  /// The model chosen in the chat picker, or null to follow the platform's chat routing. Writable, unlike
  /// contextWindow and chatModel above.
  chatModelId: string | null;
```

and `UpdateUserSettings` with `chatModelId?: string | null;` (an empty-guid string clears - see Step 2).

- [ ] **Step 2: Add the API methods**

In `apps/web/src/lib/api.ts`, in the chat section:

```ts
  /// The models this user may choose between for chat. Never carries an endpoint or a key.
  async listChatModels(): Promise<ChatModelOption[]> {
    const { data } = await http.get<ChatModelOption[]>("/api/chat/models");
    return data;
  },
```

In the platform-LLM-models section:

```ts
  /// Whether a model appears in the chat picker. Its own route rather than a field on updateModel, so a
  /// save from the editor drawer cannot post a stale value and silently un-offer the model.
  async setModelChatEnabled(id: string, enabled: boolean): Promise<void> {
    await http.put(`/api/admin/llm-models/${id}/chat-enabled`, { enabled });
  },
```

And add `modelId?: string | null;` to the `chatStream` `body` parameter type. The body is passed straight to `JSON.stringify`, so nothing else changes.

> **No "clear my choice" constant.** The server accepts an empty GUID on `chatModelId` as a clear, and Task 7 tests it - but the picker always selects *something*, so no web code ever sends it. Do not add an unused exported constant for it: `noUnusedLocals` would flag its import, and an export nothing calls invites a future reader to wire up a clear path that has no UI.

- [ ] **Step 3: Typecheck**

```bash
cd apps/web && npm run build
```

Expected: passes. (No test in this task - it is pure type and transport surface, exercised by Tasks 9-11.)

- [ ] **Step 4: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/lib/api.ts
git commit -m "feat: web types and client methods for chat model selection"
```

---

### Task 9: Routing grid - labels and the `In chat` column

**Files:**
- Modify: `apps/web/src/components/llmmodels/RoutingMatrix.tsx`
- Modify: `apps/web/src/pages/LlmModels.tsx` (pass the new handler)
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json`
- Test: `apps/web/src/components/llmmodels/RoutingMatrix.test.tsx` (append)

**Interfaces:**
- Consumes: `LlmModel.displayName`, `LlmModel.chatEnabled` (Task 8); `api.setModelChatEnabled` (Task 8).
- Produces: `RoutingMatrix` prop `onChatEnabledChange: (modelId: string, enabled: boolean) => void`.

- [ ] **Step 1: Write the failing tests**

Append to `apps/web/src/components/llmmodels/RoutingMatrix.test.tsx`. Extend the existing `models` fixture with `displayName` and `chatEnabled` first (every existing test will need those fields once the type requires them).

```tsx
it("shows the display name with the slug beneath it", () => {
  render(
    <RoutingMatrix
      {...baseProps}
      models={[{ ...models[1], displayName: "QWEN 3.8" }]}
    />,
  );

  expect(screen.getByText("QWEN 3.8")).toBeTruthy();
  expect(screen.getByText(/qwen3-27b/)).toBeTruthy();
});

it("offers an in-chat checkbox per model", () => {
  const onChatEnabledChange = vi.fn();
  render(<RoutingMatrix {...baseProps} onChatEnabledChange={onChatEnabledChange} />);

  const box = screen.getByRole("checkbox", { name: /qwen3-27b/i });
  fireEvent.click(box);

  expect(onChatEnabledChange).toHaveBeenCalledWith("b", true);
});

it("ticks and disables the chat default so the picker can never exclude it", () => {
  // The default is the model actually serving chat. An administrator who could un-offer it would leave
  // the picker unable to show the current selection.
  render(
    <RoutingMatrix {...baseProps} assignments={{ Chat: "a" }} onChatEnabledChange={vi.fn()} />,
  );

  const box = screen.getByRole("checkbox", { name: /gpt-oss-20b/i }) as HTMLInputElement;
  expect(box.checked).toBe(true);
  expect(box.disabled).toBe(true);
});

it("does not call the handler for the disabled default", () => {
  const onChatEnabledChange = vi.fn();
  render(
    <RoutingMatrix {...baseProps} assignments={{ Chat: "a" }} onChatEnabledChange={onChatEnabledChange} />,
  );

  fireEvent.click(screen.getByRole("checkbox", { name: /gpt-oss-20b/i }));

  expect(onChatEnabledChange).not.toHaveBeenCalled();
});
```

Define `baseProps` from the props the existing tests already pass, so the new tests do not duplicate the whole set.

- [ ] **Step 2: Run and watch it fail**

```bash
cd apps/web && npx vitest run src/components/llmmodels/RoutingMatrix.test.tsx
```

Expected: fails - no checkbox role in the tree.

- [ ] **Step 3: Implement**

In `RoutingMatrix.tsx`:

Add to `Props`:

```tsx
  /// Toggles whether a model appears in the chat picker. Separate from `onRoute` because it writes a
  /// different resource: routing replaces the whole assignment set, this flips one model's flag.
  onChatEnabledChange: (modelId: string, enabled: boolean) => void;
```

Widen the grid and the minimum width:

```tsx
  const grid = "grid grid-cols-[minmax(0,1fr)_repeat(7,86px)_76px_128px] items-center";
```

and `min-w-[900px]` becomes `min-w-[976px]`.

Derive the chat default and render the column. Add this above the `return`:

```tsx
  /// The model serving chat: its own assignment, else the platform default. It is offered in the picker
  /// implicitly, so its checkbox is ticked and locked.
  const chatDefaultId = assignments["Chat"] ?? defaultModelId;
```

In the header row, insert before the trailing `<span />`:

```tsx
          <span className="px-1 text-center text-[10.5px] font-semibold leading-tight tracking-wide text-gray-500 dark:text-gray-400">
            {t("llmModelsColInChat")}
          </span>
```

In each model row, insert between the `columns.map(...)` block and the action buttons:

```tsx
            <div className="flex justify-center">
              <input
                type="checkbox"
                aria-label={t("llmModelsInChatAria", { model: m.displayName || m.name })}
                title={m.id === chatDefaultId ? t("llmModelsInChatLocked") : undefined}
                checked={m.chatEnabled || m.id === chatDefaultId}
                disabled={m.id === chatDefaultId}
                onChange={(e) => onChatEnabledChange(m.id, e.target.checked)}
                className="size-4 accent-indigo-600 disabled:opacity-60"
              />
            </div>
```

In the `No model` row, insert a blank `<span />` in the same position.

Change the model name block to lead with the label:

```tsx
                <span className="truncate text-[13.5px] font-semibold tracking-tight text-gray-900 dark:text-gray-100">
                  {m.displayName || m.name}
                </span>
```

and the subtitle to name the slug when a label is set:

```tsx
              <p className="mt-[3px] truncate pl-[15px] text-[11px] tabular-nums text-gray-500 dark:text-gray-400">
                {m.displayName ? `${m.name} · ` : ""}{m.apiBase} · {m.contextLength.toLocaleString()} ctx
              </p>
```

Update the footer guidance key's value (Step 5) - the key name stays `llmModelsRoutingFooter`.

- [ ] **Step 4: Wire the page**

In `apps/web/src/pages/LlmModels.tsx`, add the mutation and pass it down:

```tsx
  const setChatEnabled = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      api.setModelChatEnabled(id, enabled),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["llm-models"] }),
    onError: (e) => setError(apiErrorMessage(e, t("llmModelsAssignError"))),
  });
```

and on `<RoutingMatrix ... onChatEnabledChange={(id, enabled) => setChatEnabled.mutate({ id, enabled })} />`.

- [ ] **Step 5: Add the i18n keys**

In `apps/web/src/locales/en/account.json`, near the other `llmModels*` keys - plain hyphens only:

```json
  "llmModelsColInChat": "In chat",
  "llmModelsInChatAria": "Offer {{model}} in the chat model picker",
  "llmModelsInChatLocked": "The chat model is always offered in the picker.",
  "llmModelsRoutingFooter": "One dot per column - each call type runs on exactly one model. Chat also offers every model ticked under In chat.",
  "llmModelsDisplayName": "Display name",
  "llmModelsDisplayNameHint": "Shown to users in the chat model picker. Leave blank to use the model name.",
```

Add the same keys to `de`, `es` and `fr`, translated, and update their existing `llmModelsRoutingFooter` value to match the new English meaning.

- [ ] **Step 6: Run and watch it pass**

```bash
cd apps/web && npx vitest run src/components/llmmodels/RoutingMatrix.test.tsx
```

- [ ] **Step 7: Mutation-verify**

Remove `disabled={m.id === chatDefaultId}`. Re-run: both default-row tests must fail. Restore in place.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/components/llmmodels/RoutingMatrix.tsx apps/web/src/components/llmmodels/RoutingMatrix.test.tsx apps/web/src/pages/LlmModels.tsx apps/web/src/locales
git commit -m "feat: model labels and an In-chat column in the routing grid"
```

---

### Task 10: Display-name field in the editor drawer

**Files:**
- Modify: `apps/web/src/components/llmmodels/ModelEditorDrawer.tsx:69,89,160,238,371-420`
- Test: `apps/web/src/components/llmmodels/ModelEditorDrawer.test.tsx` (append)

**Interfaces:**
- Consumes: `LlmModelUpsert.displayName` (Task 8), `llmModelsDisplayName` i18n keys (Task 9).

- [ ] **Step 1: Write the failing test**

```tsx
it("saves the display name and leaves blank meaning the slug", async () => {
  const saved = vi.fn();
  (api.updateModel as Mock).mockResolvedValue(models[0]);
  render(<ModelEditorDrawer {...baseProps} model={models[0]} onSaved={saved} />);

  fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: "QWEN 3.8" } });
  fireEvent.click(screen.getByRole("button", { name: /save/i }));

  await waitFor(() => expect(api.updateModel).toHaveBeenCalled());
  expect((api.updateModel as Mock).mock.calls[0][1].displayName).toBe("QWEN 3.8");
});
```

Match the file's existing mocking and prop-building conventions; `baseProps` should mirror what its current tests pass.

- [ ] **Step 2: Run and watch it fail**

```bash
cd apps/web && npx vitest run src/components/llmmodels/ModelEditorDrawer.test.tsx
```

- [ ] **Step 3: Implement**

Add the state next to `contextLength`:

```tsx
  const [displayName, setDisplayName] = useState(model?.displayName ?? "");
```

Include it in the dirty check alongside the other fields:

```tsx
    displayName !== (model?.displayName ?? "") ||
```

Include it in the save payload next to `contextLength`:

```tsx
        displayName: displayName.trim() || null,
```

Thread `displayName` / `setDisplayName` through the same props path `contextLength` uses, and render the field under `Name`:

```tsx
      <label className="block text-xs">
        <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsDisplayName")}</span>
        <input
          type="text"
          value={displayName}
          placeholder={name}
          onChange={(e) => setDisplayName(e.target.value)}
          className="w-full rounded-md border border-gray-300 px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800"
        />
        <span className="mt-1 block text-[11px] text-gray-500 dark:text-gray-400">
          {t("llmModelsDisplayNameHint")}
        </span>
      </label>
```

The placeholder is the live `name` state, so it tracks a slug the administrator is editing in the same session.

- [ ] **Step 4: Run, watch pass, mutation-verify**

Change the payload line to `displayName: null`. Re-run: the new test must fail. Restore in place.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/llmmodels/ModelEditorDrawer.tsx apps/web/src/components/llmmodels/ModelEditorDrawer.test.tsx
git commit -m "feat: edit a model's display name in the editor drawer"
```

---

### Task 11: The `ChatModelPicker` component

**Files:**
- Create: `apps/web/src/components/ChatModelPicker.tsx`
- Create: `apps/web/src/components/ChatModelPicker.test.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/chat.json`

**Interfaces:**
- Consumes: `ChatModelOption` (Task 8).
- Produces: default export `ChatModelPicker`, props
  `{ models: ChatModelOption[]; selectedId: string | null; disabled?: boolean; onSelect: (id: string) => void }`.

- [ ] **Step 1: Write the failing tests**

Create `apps/web/src/components/ChatModelPicker.test.tsx`:

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import ChatModelPicker from "./ChatModelPicker";
import type { ChatModelOption } from "../lib/types";

const models: ChatModelOption[] = [
  { id: "a", label: "GPT OSS 20B", name: "openai/gpt-oss-20b", contextLength: 131072, isDefault: true },
  { id: "b", label: "QWEN 3.8", name: "qwen3.8-27b@q4_k_xl", contextLength: 200000, isDefault: false },
];

describe("ChatModelPicker", () => {
  it("lists each model's label with its context length in brackets", () => {
    render(<ChatModelPicker models={models} selectedId="a" onSelect={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /model/i }));

    const row = screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ });
    expect(row.textContent).toContain("QWEN 3.8");
    expect(row.textContent).toContain("(200,000 ctx)");
  });

  it("never shows the raw slug", () => {
    // The slug is what the endpoint needs, not what a person choosing a model should have to read.
    render(<ChatModelPicker models={models} selectedId="a" onSelect={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /model/i }));

    expect(screen.queryByText(/qwen3\.8-27b@q4_k_xl/)).toBeNull();
  });

  it("reports the chosen model and closes", () => {
    const onSelect = vi.fn();
    render(<ChatModelPicker models={models} selectedId="a" onSelect={onSelect} />);
    fireEvent.click(screen.getByRole("button", { name: /model/i }));
    fireEvent.click(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }));

    expect(onSelect).toHaveBeenCalledWith("b");
    expect(screen.queryByRole("menuitemradio")).toBeNull();
  });

  it("marks the current selection", () => {
    render(<ChatModelPicker models={models} selectedId="b" onSelect={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /model/i }));

    expect(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }).getAttribute("aria-checked")).toBe("true");
    expect(screen.getByRole("menuitemradio", { name: /GPT OSS 20B/ }).getAttribute("aria-checked")).toBe("false");
  });

  it("does not open while disabled", () => {
    // Switching model mid-stream would change the model behind a reply already arriving.
    render(<ChatModelPicker models={models} selectedId="a" disabled onSelect={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /model/i }));

    expect(screen.queryByRole("menuitemradio")).toBeNull();
  });

  it("renders even with a single model", () => {
    // Always visible, so the toolbar layout never shifts as models are added or removed.
    render(<ChatModelPicker models={[models[0]]} selectedId="a" onSelect={vi.fn()} />);
    expect(screen.getByRole("button", { name: /model/i })).toBeTruthy();
  });
});
```

- [ ] **Step 2: Run and watch it fail**

```bash
cd apps/web && npx vitest run src/components/ChatModelPicker.test.tsx
```

Expected: fails - the module does not exist.

- [ ] **Step 3: Implement**

Create `apps/web/src/components/ChatModelPicker.tsx`:

```tsx
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { ChatModelOption } from "../lib/types";

interface Props {
  models: ChatModelOption[];
  selectedId: string | null;
  /// True while a reply is streaming: switching then would change the model behind an answer already
  /// arriving, and the turn is already in flight with the old one.
  disabled?: boolean;
  onSelect: (id: string) => void;
}

/// Chooses which model answers the next chat turn.
///
/// Shown even when there is only one model to pick, so the affordance is discoverable and the toolbar's
/// layout does not shift as an administrator adds or removes models. Rows carry the LABEL, never the slug:
/// the slug is what the endpoint needs, not what someone choosing a model should have to read.
export default function ChatModelPicker({ models, selectedId, disabled = false, onSelect }: Props) {
  const { t } = useTranslation("chat");
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onDocument(e: MouseEvent) {
      if (!ref.current?.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onDocument);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDocument);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const selected = models.find((m) => m.id === selectedId) ?? models.find((m) => m.isDefault) ?? null;

  return (
    <div ref={ref} className="relative flex items-center">
      <button
        type="button"
        aria-label={t("modelPicker", { model: selected?.label ?? "" })}
        title={t("modelPicker", { model: selected?.label ?? "" })}
        aria-expanded={open}
        disabled={disabled}
        onClick={() => !disabled && setOpen((v) => !v)}
        className="rounded p-1 text-indigo-500 hover:bg-gray-100 disabled:opacity-40 dark:text-indigo-400 dark:hover:bg-gray-700"
      >
        <SparkleIcon />
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 top-full z-30 mt-1 max-h-64 w-64 overflow-y-auto rounded-md border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-800"
        >
          {models.map((m) => (
            <button
              key={m.id}
              type="button"
              role="menuitemradio"
              aria-checked={m.id === (selected?.id ?? null)}
              onClick={() => {
                onSelect(m.id);
                setOpen(false);
              }}
              className="block w-full px-3 py-1.5 text-left text-sm hover:bg-gray-100 dark:text-gray-200 dark:hover:bg-gray-700"
            >
              <span className="truncate">{m.label}</span>{" "}
              <span className="text-[11px] tabular-nums text-gray-500 dark:text-gray-400">
                ({m.contextLength.toLocaleString()} {t("ctxSuffix")})
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/// A four-point sparkle. Inline like ChatPanel's other toolbar icons rather than a dependency, and filled
/// rather than stroked so it still reads at 16px.
const SparkleIcon = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
    <path d="M12 2l2.2 6.1L20.4 10l-6.2 1.9L12 18l-2.2-6.1L3.6 10l6.2-1.9L12 2z" />
    <path d="M18.5 15l.9 2.5 2.6.8-2.6.8-.9 2.5-.9-2.5-2.6-.8 2.6-.8.9-2.5z" opacity="0.7" />
  </svg>
);
```

- [ ] **Step 4: Add the i18n keys**

In `apps/web/src/locales/en/chat.json`:

```json
  "modelPicker": "Model: {{model}}",
  "ctxSuffix": "ctx",
```

Add translated equivalents to `de`, `es` and `fr`.

- [ ] **Step 5: Run, watch pass, mutation-verify**

```bash
cd apps/web && npx vitest run src/components/ChatModelPicker.test.tsx
```

Then change the row's content to `{m.name}`. Re-run: `never shows the raw slug` and the label tests must fail. Restore in place.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/ChatModelPicker.tsx apps/web/src/components/ChatModelPicker.test.tsx apps/web/src/locales
git commit -m "feat: add the chat model picker component"
```

---

### Task 12: Wire the picker into `ChatPanel`

**Files:**
- Modify: `apps/web/src/components/ChatPanel.tsx` (imports, state near `:87-105`, derived values `:221-227`, the stream call near `:499`, the toolbar `:733-737`, save/restore `:600-650`)
- Modify: `apps/web/src/components/ChatPanel.test.tsx` (the `vi.mock` factory, plus new tests)

**Interfaces:**
- Consumes: `api.listChatModels`, `api.updateUserSettings`, `api.chatStream({ modelId })` (Task 8); `ChatModelPicker` (Task 11).

- [ ] **Step 1: Add `listChatModels` to the test mock, then write the failing tests**

**First**, add `listChatModels: vi.fn(),` to the `vi.mock("../lib/api", ...)` factory at the top of `ChatPanel.test.tsx`, and have `beforeEach` resolve it with a two-model list. Omitting it makes every existing test in the file fail with "not a function" rather than with the assertion under test.

```tsx
const chatModels = [
  { id: "a", label: "GPT OSS 20B", name: "openai/gpt-oss-20b", contextLength: 131072, isDefault: true },
  { id: "b", label: "QWEN 3.8", name: "qwen3.8-27b@q4_k_xl", contextLength: 200000, isDefault: false },
];
```

Then add:

```tsx
it("updates the context dial as soon as a model is picked, before any turn", async () => {
  // The dial has to move on selection. Waiting for the next turn's meta event would show the previous
  // model's window for as long as the user sat there reading it.
  renderPanel();
  await waitFor(() => expect(api.listChatModels).toHaveBeenCalled());

  fireEvent.click(screen.getByRole("button", { name: /model/i }));
  fireEvent.click(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }));

  await waitFor(() => expect(screen.getByText(/200,000/)).toBeTruthy());
  expect(api.chatStream).not.toHaveBeenCalled();
});

it("sends the picked model on the next turn", async () => {
  renderPanel();
  await waitFor(() => expect(api.listChatModels).toHaveBeenCalled());

  fireEvent.click(screen.getByRole("button", { name: /model/i }));
  fireEvent.click(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }));
  fireEvent.change(screen.getByRole("textbox"), { target: { value: "hello" } });
  fireEvent.click(screen.getByRole("button", { name: /send/i }));

  await waitFor(() => expect(api.chatStream).toHaveBeenCalled());
  expect((api.chatStream as Mock).mock.calls[0][0].modelId).toBe("b");
});

it("remembers the choice as a user setting", async () => {
  renderPanel();
  await waitFor(() => expect(api.listChatModels).toHaveBeenCalled());

  fireEvent.click(screen.getByRole("button", { name: /model/i }));
  fireEvent.click(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }));

  await waitFor(() =>
    expect(api.updateUserSettings).toHaveBeenCalledWith(expect.objectContaining({ chatModelId: "b" })),
  );
});

it("keeps the label on the dial after a turn reports the slug", async () => {
  // The stream's meta event carries the slug the endpoint needs. Rendering it raw would make the dial flip
  // from "QWEN 3.8" to "qwen3.8-27b@q4_k_xl" the instant the first token arrived.
  (api.chatStream as Mock).mockImplementation(async (_body, handlers) => {
    handlers.onMeta?.({ model: "qwen3.8-27b@q4_k_xl", contextUsed: 10, contextTotal: 200000 });
    handlers.onToken("hi");
    return { model: "qwen3.8-27b@q4_k_xl", contextUsed: 12, contextTotal: 200000 };
  });
  renderPanel();
  await waitFor(() => expect(api.listChatModels).toHaveBeenCalled());

  fireEvent.click(screen.getByRole("button", { name: /model/i }));
  fireEvent.click(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }));
  fireEvent.change(screen.getByRole("textbox"), { target: { value: "hello" } });
  fireEvent.click(screen.getByRole("button", { name: /send/i }));

  await waitFor(() => expect(api.chatStream).toHaveBeenCalled());
  expect(screen.queryByText(/qwen3\.8-27b@q4_k_xl/)).toBeNull();
});
```

Adapt `renderPanel`, the send-button name and the meta shape to whatever the file already uses - do not invent new helpers. The dial's model text lives in a hover tooltip that is rendered but hidden, so `queryByText` still finds it if present.

- [ ] **Step 2: Run and watch them fail**

```bash
cd apps/web && npx vitest run src/components/ChatPanel.test.tsx
```

- [ ] **Step 3: Implement**

Import the picker (the existing `api` import is unchanged):

```tsx
import ChatModelPicker from "./ChatModelPicker";
```

Add the query and the state near the other `useQuery`/`useState` declarations:

```tsx
  // The models an administrator offers for chat. Always fetched, even for a single-model platform: the
  // picker is always shown, so there is always a list to render.
  const { data: chatModels } = useQuery({ queryKey: ["chat-models"], queryFn: api.listChatModels });

  // The chosen model. Seeded from the remembered user setting once it loads, then owned locally so a pick
  // takes effect immediately rather than after a settings refetch.
  const [modelId, setModelId] = useState<string | null>(null);
  const seededModel = useRef(false);
  useEffect(() => {
    if (seededModel.current || settings === undefined) return;
    seededModel.current = true;
    setModelId(settings.chatModelId ?? null);
  }, [settings]);
```

Add the derived values, replacing the existing `dialTotal` / `dialModel` lines:

```tsx
  const models = chatModels ?? [];
  const selectedModel = models.find((m) => m.id === modelId) ?? models.find((m) => m.isDefault) ?? null;

  // Prefer the picked model's own window so the dial moves the moment a model is chosen, rather than at
  // the next turn. Once a turn has run the server reports the same number for that model anyway.
  const dialTotal = selectedModel?.contextLength || usage?.contextTotal || settings?.contextWindow || 0;
  const dialUsed = usage?.contextUsed ?? 0;
  // The stream reports the SLUG. Map it back to a label, or the dial would flip from the readable name to
  // the raw slug as soon as the first turn's meta event landed.
  const dialModel =
    selectedModel?.label ??
    models.find((m) => m.name === usage?.model)?.label ??
    usage?.model ??
    settings?.chatModel ??
    "";
```

Add the selection handler:

```tsx
  /// Remembers the pick for next time. Fire and forget: the local state has already moved, and a failed
  /// save should not undo a choice the user can simply make again.
  function chooseModel(id: string) {
    setModelId(id);
    api.updateUserSettings({ chatModelId: id }).then(
      () => qc.invalidateQueries({ queryKey: ["user-settings"] }),
      () => {},
    );
  }
```

Render the picker in the toolbar, in the `ml-auto` group before the dial:

```tsx
        <div className="ml-auto flex items-center gap-1">
          <ChatModelPicker
            models={models}
            selectedId={selectedModel?.id ?? null}
            disabled={streaming}
            onSelect={chooseModel}
          />
          {dialTotal > 0 && <ContextDial model={dialModel} used={dialUsed} total={dialTotal} />}
        </div>
```

Add `modelId` to the `api.chatStream` body, next to `searchAllMeetings`:

```tsx
          modelId: selectedModel?.id ?? null,
```

In `saveConversation`, add to the `context` object:

```tsx
        modelId: selectedModel?.id ?? null,
```

In `openConversation`, restore it - falling back when the model is no longer offered:

```tsx
      // A model can be un-ticked between saving and reopening. Falling back to the default is the same
      // rule the server applies, so the dial and the reply agree.
      const savedModel = c.context.modelId;
      setModelId(savedModel && models.some((m) => m.id === savedModel) ? savedModel : null);
```

Add `modelId?: string | null` to the saved-context type in `apps/web/src/lib/types.ts` if it is declared there.

Finally, update the `/context` slash command's model line at `:412` - it already reads `dialModel`, so it inherits the label automatically. Verify no other call site formats a raw model slug.

- [ ] **Step 4: Run the full web suite**

```bash
cd apps/web && npm test
```

Expected: green. Any pre-existing test that breaks with "listChatModels is not a function" means a `vi.mock` factory elsewhere needs the method added - `SettingsModal.test.tsx` and `AssistantSection.test.tsx` also stub the api and may render a `ChatPanel`.

- [ ] **Step 5: Mutation-verify**

Change `dialModel` to `usage?.model ?? selectedModel?.label ?? ""`. Re-run: `keeps the label on the dial after a turn reports the slug` must fail. Restore in place. Then remove `modelId` from the stream body and confirm `sends the picked model on the next turn` fails. Restore.

- [ ] **Step 6: Typecheck and commit**

```bash
cd apps/web && npm run build
git add apps/web/src/components/ChatPanel.tsx apps/web/src/components/ChatPanel.test.tsx apps/web/src/lib/types.ts
git commit -m "feat: choose a chat model from the chat panel"
```

---

### Task 13: Live verification

**Files:** none - this task changes nothing. It exists because several of this feature's invariants are invisible to the test suite.

> jsdom computes no geometry, so no test above proves the new grid column fits, and no test proves the picker's dropdown is not clipped by the chat panel's `overflow` container. Both are the kind of defect that only appears in a browser. The web app also uses **axios/XHR**, so fetch-level stubs would not intercept its calls.

- [ ] **Step 1: Start the stack and open the app**

Use the local docker stack. Rebuild the `api` container after the API changes - redis is unpublished, so the API cannot be run from the host against it.

- [ ] **Step 2: Check the routing grid at `/admin/llm-models`**

Confirm: the `In chat` column is present and not clipped; the header aligns with its checkboxes; the chat default's box is ticked and greyed; a model with a display name shows the label bold with the slug in the subtitle; the footer text reads the new sentence. Measure with `getBoundingClientRect` if anything looks tight - a class-presence assertion proves nothing about layout.

- [ ] **Step 3: Check the picker**

Tick a second model. In the chat panel, confirm the sparkle is purple and visible, the dropdown opens **below** it and is not clipped by the panel, rows show `Label (n ctx)` and no slug, and the dial's total changes on selection.

- [ ] **Step 4: Switch mid-conversation**

Send a turn, switch model, send another. Confirm the second reply comes from the new model (check the usage log filtered on `Kind = ChatMessage` - **note `LlmCallKind.ChatMessage = 12`, not the `LlmCallGroup.Chat = 6`**; querying the group number returns an empty result that reads as "it never ran"). Confirm the dial keeps showing the label after the reply lands.

- [ ] **Step 5: Check the fallbacks**

Un-tick the chosen model in the admin grid, reload chat, confirm the picker falls back to the default and a new turn is answered by it - and that re-ticking restores the earlier choice.

- [ ] **Step 6: Record the outcome**

Note in the PR description what was verified live and what was not. Do not claim anything you did not observe.

---

### Task 14: Release bookkeeping and docs (PR 1)

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`
- Modify: `docs/Overall_Synopsis_of_Platform.md`, `docs/Data_Schema.md`
- Modify: `apps/web/src/content/help/en/chat-over-transcripts.md`, `apps/web/src/content/help/en/ai-model-settings.md`

- [ ] **Step 1: Bump the version to `0.231.0` in all five places**

A minor bump with Build reset, because this is a functional enhancement. `versionMirrors.test.ts` fails the build if any mirror drifts - including the n8n node, which npm cannot correct after publishing.

- [ ] **Step 2: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`. The `pr` number cannot be guessed as "last + 1" - Dependabot PRs and issues share the sequence, and no test catches a wrong number. Get the real one:

```bash
gh pr list --state all --limit 1 --json number
```

then add one and confirm after opening the PR.

```ts
  {
    version: "0.231.0",
    date: "2026-08-20",
    pr: 0, // replace with the real number
    headline: "Choose which AI model answers your chat",
    summary:
      "Chat used to run on the single model an administrator assigned to it. Now an administrator can " +
      "tick any number of models as available for chat, and each person picks between them from a new " +
      "control in the chat toolbar - including part-way through a conversation, which carries the " +
      "history across to the new model. Models can also be given a readable display name, so the picker " +
      "shows \"QWEN 3.8\" rather than the raw model identifier the server needs. Your choice is " +
      "remembered, and is stored with a saved conversation so reopening one puts you back on the model " +
      "it was using.",
    added: [
      "A model picker in the chat toolbar, showing each model's name and context window.",
      "An In chat column in the model routing grid, marking which models the picker offers.",
      "A display name per model, shown to users in place of the model identifier.",
    ],
    changed: [
      "The chat context gauge now follows the model you have chosen, rather than the platform's default.",
    ],
  },
```

- [ ] **Step 3: Update the About-box `CAPABILITIES` table**

Edit the Chat row in `releases.ts` to mention model choice. Keep it a one-line table row; do not reintroduce prose.

- [ ] **Step 4: Update the README Features row and `docs/features.md` in lockstep**

Never one without the other. The README row is one concise line; `docs/features.md` carries the full prose bullet.

- [ ] **Step 5: Update `docs/Overall_Synopsis_of_Platform.md`**

Add: `GET /api/chat/models`; `modelId` on the chat stream contract; `ChatModelCatalog` as the single authority on chat-offered models; and the note that `ChatTitle` deliberately stays on the chat default.

- [ ] **Step 6: Update `docs/Data_Schema.md`**

Add `LlmModels.DisplayName`, `LlmModels.ChatEnabled`, `UserSettings.ChatModelId` with its `SET NULL` FK, plus a migration-history row for `ChatModelSelection`. State that `CurrentFormat` was not bumped, and why.

- [ ] **Step 7: Update the two help articles**

`chat-over-transcripts.md`: how to choose a model, that switching mid-conversation carries the history, and that a saved conversation remembers its model. `ai-model-settings.md`: display names, the `In chat` column, and that the chat default is always offered. ASCII only, keep the front-matter `summary` to two or three sentences - it is what the contextual `?` popover shows.

- [ ] **Step 8: Run everything**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
cd apps/web && npm test && npm run build
```

Expected: all green, no warnings. Run the integration suite too if Docker is available.

- [ ] **Step 9: Check for stray em dashes**

Windows pipe decoding hides them: `git diff | python` decodes cp1252 on this machine and reports a false zero. Write the diff to a file and decode UTF-8 explicitly.

```bash
git diff main --stat > /dev/null; git diff main > /tmp/pr1.diff
python -c "import io;d=io.open('/tmp/pr1.diff',encoding='utf-8').read();print([l for l in d.splitlines() if ('—' in l or '–' in l)])"
```

Expected: `[]`, ignoring any hit inside a code comment or an internal doc.

- [ ] **Step 10: Commit, push, open the PR**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md docs/Overall_Synopsis_of_Platform.md docs/Data_Schema.md apps/web/src/content/help/en/chat-over-transcripts.md apps/web/src/content/help/en/ai-model-settings.md
git commit -m "chore: release 0.231.0"
git push -u origin feat/chat-model-selection
```

Then open the PR with a body containing these five headings, filled in from what actually happened:

- **What changed** - the picker, the In-chat column, display names, and that the chat dot's routing semantics are unchanged.
- **Deployment surface** - "Server redeploy only. Nothing under `apps/desktop/src`, so no desktop release is needed."
- **Schema** - the three additive columns and the `SET NULL` FK; state explicitly that `MaintenanceController.CurrentFormat` was **not** bumped and why (additive nullable columns are forward-restore-safe).
- **Security note** - that `ChatModelCatalog` is the single check preventing a user routing chat at an arbitrary configured endpoint, and that it lives in the resolver rather than the controller.
- **Verified live / not verified** - exactly what Task 13 confirmed in a browser, and anything it could not.

```bash
gh pr create --title "Choose which AI model answers your chat" --body-file /tmp/pr1-body.md
```

Then correct the `pr:` number in `releases.ts` and push the fix.

---

# PR 2 - add all from an endpoint

Start from a fresh branch off `main` once PR 1 has merged (or off `main` directly - PR 2 has no dependency on PR 1's columns).

```bash
git checkout main && git pull && git checkout -b feat/discover-models-from-endpoint
```

### Task 15: The pure discovery parser and filter

**Files:**
- Create: `src/Diariz.Api/Services/Llm/LlmModelDiscovery.cs`
- Test: `tests/Diariz.Api.Tests/Llm/LlmModelDiscoveryTests.cs`

**Interfaces:**
- Produces:
  - `record DiscoveredModel(string Id, int? ContextLength, string? Kind)`
  - `static IReadOnlyList<DiscoveredModel> LlmModelDiscovery.ParseLmStudio(string json)`
  - `static IReadOnlyList<DiscoveredModel> LlmModelDiscovery.ParseOpenAi(string json)`
  - `static bool LlmModelDiscovery.IsChatModel(DiscoveredModel model)`
  - `const int LlmModelDiscovery.DefaultContextLength = 16384`

> Pure and HTTP-free on purpose, the same separation the Python worker uses for `_shape_segments`: the interesting logic is the filtering, and it should be testable without a server to talk to.

- [ ] **Step 1: Write the failing tests**

```csharp
using Diariz.Api.Services.Llm;

namespace Diariz.Api.Tests.Llm;

public class LlmModelDiscoveryTests
{
    private const string LmStudioJson = """
        {"data":[
          {"id":"qwen3.8-27b@q4_k_xl","type":"llm","max_context_length":200000},
          {"id":"text-embedding-nomic-embed-text-v2","type":"embeddings","max_context_length":2048},
          {"id":"gemma-3-27b-vision","type":"vlm","max_context_length":8192}
        ]}
        """;

    private const string OpenAiJson = """
        {"object":"list","data":[
          {"id":"gpt-4o","object":"model"},
          {"id":"text-embedding-3-large","object":"model"},
          {"id":"whisper-1","object":"model"}
        ]}
        """;

    [Fact]
    public void Reads_lm_studios_type_and_context_length()
    {
        var models = LlmModelDiscovery.ParseLmStudio(LmStudioJson);

        Assert.Equal(3, models.Count);
        Assert.Equal(200_000, models[0].ContextLength);
        Assert.Equal("embeddings", models[1].Kind);
    }

    [Fact]
    public void Keeps_a_vision_model_it_is_still_a_chat_model()
    {
        var vlm = LlmModelDiscovery.ParseLmStudio(LmStudioJson).Single(m => m.Kind == "vlm");
        Assert.True(LlmModelDiscovery.IsChatModel(vlm));
    }

    [Fact]
    public void Drops_an_embeddings_model_by_its_declared_type()
    {
        var embed = LlmModelDiscovery.ParseLmStudio(LmStudioJson).Single(m => m.Kind == "embeddings");
        Assert.False(LlmModelDiscovery.IsChatModel(embed));
    }

    [Fact]
    public void Reads_an_openai_listing_with_no_type_or_context_length()
    {
        var models = LlmModelDiscovery.ParseOpenAi(OpenAiJson);

        Assert.Equal(["gpt-4o", "text-embedding-3-large", "whisper-1"], models.Select(m => m.Id));
        Assert.All(models, m => Assert.Null(m.ContextLength));
        Assert.All(models, m => Assert.Null(m.Kind));
    }

    [Theory]
    [InlineData("text-embedding-3-large")]
    [InlineData("nomic-embed-text-v2")]
    [InlineData("bge-reranker-base")]
    [InlineData("whisper-1")]
    [InlineData("kokoro-tts")]
    [InlineData("clip-vit-base")]
    public void Drops_a_non_chat_model_by_name_when_no_type_is_reported(string id)
    {
        // The OpenAI-compatible listing reports only ids, so a name heuristic is the only signal there is.
        Assert.False(LlmModelDiscovery.IsChatModel(new DiscoveredModel(id, null, null)));
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("qwen3.8-27b@q4_k_xl")]
    [InlineData("llama-3.3-70b-instruct")]
    public void Keeps_a_chat_model(string id)
    {
        Assert.True(LlmModelDiscovery.IsChatModel(new DiscoveredModel(id, null, null)));
    }

    [Fact]
    public void A_declared_type_beats_the_name_heuristic()
    {
        // A chat model whose name happens to contain "embed" must survive when the server says it is an llm.
        Assert.True(LlmModelDiscovery.IsChatModel(new DiscoveredModel("embedder-chat-7b", 4096, "llm")));
    }

    [Fact]
    public void Malformed_json_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(LlmModelDiscovery.ParseOpenAi("not json"));
        Assert.Empty(LlmModelDiscovery.ParseLmStudio("{}"));
    }
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmModelDiscoveryTests"
```

- [ ] **Step 3: Implement**

Create `src/Diariz.Api/Services/Llm/LlmModelDiscovery.cs`:

```csharp
using System.Text.Json;

namespace Diariz.Api.Services.Llm;

/// <summary>One model an endpoint reported. <see cref="ContextLength"/> and <see cref="Kind"/> are null
/// when the endpoint does not say - the OpenAI-compatible listing reports neither.</summary>
public sealed record DiscoveredModel(string Id, int? ContextLength, string? Kind);

/// <summary>Parsing and filtering for model discovery, with no HTTP in sight.
///
/// Pure so the interesting part - deciding what is a chat model - is testable without a server, the same
/// separation the worker uses for its segment shaping.</summary>
public static class LlmModelDiscovery
{
    /// <summary>What an imported model's context window is set to when the endpoint does not report one.
    /// 16k rather than the editor's 8k default: this number drives both the chat dial and the real context
    /// budget, and an import that silently under-sizes a model truncates transcript text the user believed
    /// was in scope. An administrator can correct it per model.</summary>
    public const int DefaultContextLength = 16384;

    /// <summary>Substrings that identify a non-chat model when the endpoint reports no type. A heuristic,
    /// and deliberately a conservative one: a wrongly-kept model is one row an administrator deletes, while
    /// a wrongly-dropped one is invisible.</summary>
    private static readonly string[] NonChatMarkers =
        ["embed", "rerank", "whisper", "tts", "clip", "bge-"];

    public static IReadOnlyList<DiscoveredModel> ParseLmStudio(string json) =>
        Parse(json, e => new DiscoveredModel(
            e.GetProperty("id").GetString() ?? "",
            e.TryGetProperty("max_context_length", out var c) && c.TryGetInt32(out var n) ? n : null,
            e.TryGetProperty("type", out var t) ? t.GetString() : null));

    public static IReadOnlyList<DiscoveredModel> ParseOpenAi(string json) =>
        Parse(json, e => new DiscoveredModel(e.GetProperty("id").GetString() ?? "", null, null));

    private static IReadOnlyList<DiscoveredModel> Parse(
        string json, Func<JsonElement, DiscoveredModel> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return [];

            return data.EnumerateArray()
                .Where(e => e.TryGetProperty("id", out _))
                .Select(read)
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .ToList();
        }
        catch (JsonException)
        {
            // A malformed body is a fact about the endpoint, not an error in us. Returning nothing lets the
            // caller report "no models found" rather than a 500 that says nothing about their server.
            return [];
        }
    }

    /// <summary>Whether this is a model chat could actually use.
    ///
    /// A DECLARED type wins outright: when the server says "llm" it knows better than a substring match,
    /// and a chat model whose name happens to contain "embed" must survive. Only when no type is reported
    /// does the name heuristic apply. Vision models count as chat models.</summary>
    public static bool IsChatModel(DiscoveredModel model)
    {
        if (model.Kind is { } kind)
            return kind is "llm" or "vlm";

        var id = model.Id.ToLowerInvariant();
        return !NonChatMarkers.Any(id.Contains);
    }
}
```

- [ ] **Step 4: Run, watch pass, mutation-verify**

Change `IsChatModel`'s type branch to `return true;`. Re-run: `Drops_an_embeddings_model_by_its_declared_type` must fail. Restore in place.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/Llm/LlmModelDiscovery.cs tests/Diariz.Api.Tests/Llm/LlmModelDiscoveryTests.cs
git commit -m "feat: add the pure model discovery parser and chat-model filter"
```

---

### Task 16: The discovery client and endpoints

**Files:**
- Create: `src/Diariz.Api/Services/Llm/LlmModelDiscoveryClient.cs`
- Modify: `src/Diariz.Api/Controllers/LlmModelsController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Modify: `src/Diariz.Api/Program.cs`
- Test: `tests/Diariz.Api.Tests/Llm/LlmModelDiscoveryClientTests.cs`, `tests/Diariz.Api.Tests/LlmModelsControllerTests.cs` (append)

**Interfaces:**
- Consumes: `LlmModelDiscovery` (Task 15); `CapturingHandler` from TestSupport for the HTTP tests.
- Produces:
  - `interface ILlmModelDiscoveryClient { Task<IReadOnlyList<DiscoveredModel>> ListAsync(string apiBase, string? apiKey, CancellationToken ct = default); }`
  - `record DiscoverModelsRequest(string ApiBase, string? ApiKey)`
  - `record DiscoveredModelDto(string Id, int ContextLength, bool ContextLengthReported, bool AlreadyExists)`
  - `record ImportModelsRequest(string ApiBase, string? ApiKey, IReadOnlyList<string> Names)`
  - `record ImportModelsResultDto(int Added, int Skipped, IReadOnlyList<string> NeedContextLength)`
  - `POST /api/admin/llm-models/discover`, `POST /api/admin/llm-models/discover/import`

- [ ] **Step 1: Write the failing client tests**

Create `tests/Diariz.Api.Tests/Llm/LlmModelDiscoveryClientTests.cs`. Drive it through a scripted `HttpMessageHandler` the way `LlmTestProbeTests` already does - reuse `CapturingHandler` from TestSupport if its shape fits, otherwise add a small route-scripting handler **to TestSupport**, never to the test project (this repo puts boundary fakes in TestSupport rather than reaching for a mocking library).

```csharp
using System.Net;
using Diariz.Api.Services.Llm;

namespace Diariz.Api.Tests.Llm;

public class LlmModelDiscoveryClientTests
{
    private const string LmStudioBody =
        """{"data":[{"id":"qwen3.8-27b@q4_k_xl","type":"llm","max_context_length":200000}]}""";
    private const string OpenAiBody = """{"object":"list","data":[{"id":"gpt-4o","object":"model"}]}""";

    /// <summary>Answers each path from a script and records every path asked for, so a test can assert not
    /// just what came back but which endpoint was consulted.</summary>
    private sealed class RouteHandler(Dictionary<string, (HttpStatusCode, string)> routes) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var (status, body) = routes.TryGetValue(path, out var r) ? r : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static (LlmModelDiscoveryClient client, RouteHandler handler) Build(
        Dictionary<string, (HttpStatusCode, string)> routes)
    {
        var handler = new RouteHandler(routes);
        return (new LlmModelDiscoveryClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Prefers_lm_studios_listing_and_does_not_also_call_the_openai_one()
    {
        // Only LM Studio reports a type and a real context length. Once it has answered there is nothing
        // the OpenAI-compatible listing could add, and a second call would be a second chance to fail.
        var (client, handler) = Build(new()
        {
            ["/api/v0/models"] = (HttpStatusCode.OK, LmStudioBody),
            ["/v1/models"] = (HttpStatusCode.OK, OpenAiBody),
        });

        var models = await client.ListAsync("http://lm.test/v1", null);

        var only = Assert.Single(models);
        Assert.Equal("qwen3.8-27b@q4_k_xl", only.Id);
        Assert.Equal(200_000, only.ContextLength);
        Assert.Equal("llm", only.Kind);
        Assert.DoesNotContain("/v1/models", handler.Paths);
    }

    [Fact]
    public async Task Falls_back_to_the_openai_listing()
    {
        var (client, _) = Build(new()
        {
            ["/v1/models"] = (HttpStatusCode.OK, OpenAiBody),
        });

        var only = Assert.Single(await client.ListAsync("http://plain.test/v1", null));

        Assert.Equal("gpt-4o", only.Id);
        Assert.Null(only.ContextLength);
        Assert.Null(only.Kind);
    }

    [Fact]
    public async Task Returns_nothing_when_both_listings_fail()
    {
        // A wrong URL is the main thing an administrator will hit here. Returning empty lets the endpoint
        // say "no models found"; throwing would surface a 500 from OUR api that says nothing about theirs.
        var (client, _) = Build([]);
        Assert.Empty(await client.ListAsync("http://nothing.test/v1", null));
    }

    [Fact]
    public async Task Sends_the_key_as_a_bearer_token_when_one_is_given()
    {
        var (client, handler) = Build(new() { ["/v1/models"] = (HttpStatusCode.OK, OpenAiBody) });
        // Capture the header by wrapping: extend RouteHandler with a `Headers` list if the assertion needs
        // it, or assert via CapturingHandler if that is what you reused.
        await client.ListAsync("http://plain.test/v1", "sk-secret");

        Assert.Contains("/v1/models", handler.Paths);
    }

    [Fact]
    public async Task Does_not_follow_a_redirect()
    {
        // A cooperating host must not be able to bounce this request onward to somewhere the administrator
        // never named - that is the whole reason the caller-supplied URL was contentious.
        var (client, handler) = Build(new()
        {
            ["/api/v0/models"] = (HttpStatusCode.Redirect, ""),
            ["/v1/models"] = (HttpStatusCode.Redirect, ""),
        });

        Assert.Empty(await client.ListAsync("http://redirector.test/v1", null));
        Assert.Equal(2, handler.Paths.Count);   // one attempt each, no follow-on
    }
}
```

Extend `RouteHandler` with a captured-headers list to make `Sends_the_key_as_a_bearer_token_when_one_is_given` assert the header rather than the path - the version above records only paths, so finish that assertion before moving on.

- [ ] **Step 2: Run, watch fail, then implement the client**

Create `src/Diariz.Api/Services/Llm/LlmModelDiscoveryClient.cs`. The doc comment must carry the SSRF reasoning:

```csharp
/// <summary>Asks an OpenAI-compatible server what models it has.
///
/// <b>This is the only endpoint that fetches an administrator-supplied URL.</b> Its neighbour,
/// POST /{id}/test, deliberately refuses one: accepting a caller-supplied URL turns an administrator's
/// session into a way of reaching arbitrary hosts with no model row as an audit trail. That relaxation was
/// made knowingly here, because onboarding a server with forty models by hand is the problem being solved,
/// and it is bounded rather than open:
///
///   * the route is ManagePlatform only;
///   * the request times out after 10 seconds;
///   * redirects are NOT followed, so a cooperating host cannot bounce the request onward;
///   * the response is read to a cap, so a hostile endpoint cannot stream unbounded data into memory;
///   * ONLY parsed model ids leave this class. The raw body is never returned to the caller, so this
///     cannot be used as a general-purpose fetch.
///
/// LM Studio's /api/v0/models is tried first because it reports a type and a real context length; the
/// OpenAI-compatible /models reports neither, so everything from it needs a default and a name heuristic.</summary>
```

Cap the read at 256 KB and the timeout at 10 seconds; construct the `HttpClient` with `AllowAutoRedirect = false`. Register it in `Program.cs` with `AddHttpClient`.

- [ ] **Step 3: Write the failing controller tests**

Add `FakeLlmModelDiscoveryClient` to `tests/Diariz.Api.TestSupport/Fakes.cs`:

```csharp
public sealed class FakeLlmModelDiscoveryClient : ILlmModelDiscoveryClient
{
    public List<DiscoveredModel> Models { get; set; } = [];
    public string? LastApiBase { get; private set; }
    public string? LastApiKey { get; private set; }

    public Task<IReadOnlyList<DiscoveredModel>> ListAsync(
        string apiBase, string? apiKey, CancellationToken ct = default)
    {
        LastApiBase = apiBase;
        LastApiKey = apiKey;
        return Task.FromResult<IReadOnlyList<DiscoveredModel>>(Models);
    }
}
```

Append to `tests/Diariz.Api.Tests/LlmModelsControllerTests.cs` (extend the existing `Build` helper with an optional `ILlmModelDiscoveryClient? discovery = null` parameter, defaulting to a new fake):

```csharp
    [Fact]
    public async Task Discover_marks_a_model_that_already_exists()
    {
        using var db = TestDb.Create();
        Seed(db, name: "gpt-4o");
        var discovery = new FakeLlmModelDiscoveryClient
        {
            Models = [new DiscoveredModel("gpt-4o", 128000, "llm"), new DiscoveredModel("llama-3.3-70b", null, null)],
        };

        var result = await Build(db, discovery: discovery)
            .Discover(new DiscoverModelsRequest("http://lm.test/v1", null));
        var dtos = Assert.IsType<List<DiscoveredModelDto>>(result.Value);

        Assert.True(dtos.Single(d => d.Id == "gpt-4o").AlreadyExists);
        Assert.False(dtos.Single(d => d.Id == "llama-3.3-70b").AlreadyExists);
    }

    [Fact]
    public async Task Discover_omits_non_chat_models()
    {
        using var db = TestDb.Create();
        var discovery = new FakeLlmModelDiscoveryClient
        {
            Models =
            [
                new DiscoveredModel("gpt-4o", null, null),
                new DiscoveredModel("text-embedding-3-large", null, "embeddings"),
                new DiscoveredModel("whisper-1", null, null),
            ],
        };

        var result = await Build(db, discovery: discovery)
            .Discover(new DiscoverModelsRequest("http://lm.test/v1", null));

        Assert.Equal(["gpt-4o"], Assert.IsType<List<DiscoveredModelDto>>(result.Value).Select(d => d.Id));
    }

    [Fact]
    public async Task Discover_defaults_an_unreported_context_length_and_says_so()
    {
        // The flag matters as much as the number: 16384 is a guess, and the dial and the context budget
        // both read it. An administrator who cannot tell a guess from a fact will not correct it.
        using var db = TestDb.Create();
        var discovery = new FakeLlmModelDiscoveryClient
        {
            Models = [new DiscoveredModel("gpt-4o", null, null), new DiscoveredModel("qwen", 200_000, "llm")],
        };

        var dtos = Assert.IsType<List<DiscoveredModelDto>>(
            (await Build(db, discovery: discovery).Discover(new DiscoverModelsRequest("http://lm.test/v1", null))).Value);

        var guessed = dtos.Single(d => d.Id == "gpt-4o");
        Assert.Equal(16384, guessed.ContextLength);
        Assert.False(guessed.ContextLengthReported);

        var known = dtos.Single(d => d.Id == "qwen");
        Assert.Equal(200_000, known.ContextLength);
        Assert.True(known.ContextLengthReported);
    }

    [Fact]
    public async Task Import_creates_only_the_requested_names_with_the_endpoint_and_key()
    {
        using var db = TestDb.Create();
        var discovery = new FakeLlmModelDiscoveryClient
        {
            Models = [new DiscoveredModel("gpt-4o", 128000, "llm"), new DiscoveredModel("llama-3.3-70b", null, null)],
        };

        var result = await Build(db, discovery: discovery)
            .Import(new ImportModelsRequest("http://lm.test/v1", "sk-secret", ["gpt-4o"]));

        var summary = Assert.IsType<ImportModelsResultDto>(result.Value);
        Assert.Equal(1, summary.Added);

        var created = Assert.Single(db.LlmModels);
        Assert.Equal("gpt-4o", created.Name);
        Assert.Equal("http://lm.test/v1", created.ApiBase);
        Assert.NotNull(created.ApiKeyEncrypted);
        Assert.Equal(128000, created.ContextLength);
    }

    [Fact]
    public async Task Import_skips_a_name_that_already_exists()
    {
        using var db = TestDb.Create();
        Seed(db, name: "gpt-4o");
        var discovery = new FakeLlmModelDiscoveryClient { Models = [new DiscoveredModel("gpt-4o", null, null)] };

        var summary = Assert.IsType<ImportModelsResultDto>(
            (await Build(db, discovery: discovery)
                .Import(new ImportModelsRequest("http://lm.test/v1", null, ["gpt-4o"]))).Value);

        Assert.Equal(0, summary.Added);
        Assert.Equal(1, summary.Skipped);
        Assert.Single(db.LlmModels);
    }

    [Fact]
    public async Task Import_does_not_offer_the_new_models_in_chat()
    {
        // Importing forty models from a server must not put forty rows in everyone's chat picker. Offering
        // one is a separate, deliberate act.
        using var db = TestDb.Create();
        var discovery = new FakeLlmModelDiscoveryClient { Models = [new DiscoveredModel("gpt-4o", null, null)] };

        await Build(db, discovery: discovery)
            .Import(new ImportModelsRequest("http://lm.test/v1", null, ["gpt-4o"]));

        Assert.False(Assert.Single(db.LlmModels).ChatEnabled);
    }

    [Fact]
    public async Task Import_refuses_a_name_the_endpoint_did_not_report()
    {
        // The name list comes from the client, so it is caller input. Trusting it would let an admin
        // session create a row for any model string against any endpoint without discovery having seen it.
        using var db = TestDb.Create();
        var discovery = new FakeLlmModelDiscoveryClient { Models = [new DiscoveredModel("gpt-4o", null, null)] };

        var summary = Assert.IsType<ImportModelsResultDto>(
            (await Build(db, discovery: discovery)
                .Import(new ImportModelsRequest("http://lm.test/v1", null, ["not-on-this-server"]))).Value);

        Assert.Equal(0, summary.Added);
        Assert.Empty(db.LlmModels);
    }
```

> That last test adds a rule the design did not name: **`Import` re-discovers and accepts only names the endpoint actually reported.** Without it the `names[]` list is unchecked caller input. Implement it by calling `ILlmModelDiscoveryClient.ListAsync` inside `Import` and intersecting.

- [ ] **Step 4: Implement the endpoints, run, mutation-verify, commit**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Discovery"
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmModelsControllerTests"
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
git add src/Diariz.Api/Services/Llm/LlmModelDiscoveryClient.cs src/Diariz.Api/Controllers/LlmModelsController.cs src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests tests/Diariz.Api.Tests/Snapshots
git commit -m "feat: discover and import the models on an endpoint"
```

---

### Task 17: The Add-all dialog

**Files:**
- Create: `apps/web/src/components/llmmodels/DiscoverModelsDialog.tsx`
- Create: `apps/web/src/components/llmmodels/DiscoverModelsDialog.test.tsx`
- Modify: `apps/web/src/pages/LlmModels.tsx`, `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts`
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json`

**Interfaces:**
- Consumes: `POST /api/admin/llm-models/discover` and `/discover/import` (Task 16).
- Produces:
  - `interface DiscoveredModel { id: string; contextLength: number; contextLengthReported: boolean; alreadyExists: boolean }`
  - `interface ImportModelsResult { added: number; skipped: number; needContextLength: string[] }`
  - `api.discoverModels(body: { apiBase: string; apiKey?: string | null }): Promise<DiscoveredModel[]>`
  - `api.importModels(body: { apiBase: string; apiKey?: string | null; names: string[] }): Promise<ImportModelsResult>`
  - default export `DiscoverModelsDialog`, props `{ onClose: () => void; onImported: () => void }`

- [ ] **Step 1: Write the failing tests**

Create `apps/web/src/components/llmmodels/DiscoverModelsDialog.test.tsx`:

```tsx
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

vi.mock("../../lib/api", () => ({
  api: { discoverModels: vi.fn(), importModels: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../../lib/api";
import DiscoverModelsDialog from "./DiscoverModelsDialog";

const found = [
  { id: "gpt-4o", contextLength: 128000, contextLengthReported: true, alreadyExists: false },
  { id: "llama-3.3-70b", contextLength: 16384, contextLengthReported: false, alreadyExists: false },
  { id: "already-here", contextLength: 8192, contextLengthReported: true, alreadyExists: true },
];

function open() {
  render(<DiscoverModelsDialog onClose={vi.fn()} onImported={vi.fn()} />);
  fireEvent.change(screen.getByLabelText(/endpoint/i), { target: { value: "http://lm.test/v1" } });
  fireEvent.change(screen.getByLabelText(/key/i), { target: { value: "sk-secret" } });
  fireEvent.click(screen.getByRole("button", { name: /discover/i }));
}

describe("DiscoverModelsDialog", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.discoverModels as Mock).mockResolvedValue(found);
    (api.importModels as Mock).mockResolvedValue({ added: 2, skipped: 0, needContextLength: [] });
  });

  it("queries the endpoint the administrator typed", async () => {
    open();
    await waitFor(() =>
      expect(api.discoverModels).toHaveBeenCalledWith({ apiBase: "http://lm.test/v1", apiKey: "sk-secret" }),
    );
  });

  it("pre-ticks new models and locks off ones already defined", async () => {
    open();
    await waitFor(() => expect(screen.getByRole("checkbox", { name: /gpt-4o/ })).toBeTruthy());

    expect((screen.getByRole("checkbox", { name: /gpt-4o/ }) as HTMLInputElement).checked).toBe(true);
    const existing = screen.getByRole("checkbox", { name: /already-here/ }) as HTMLInputElement;
    expect(existing.checked).toBe(false);
    expect(existing.disabled).toBe(true);
  });

  it("flags a context length the endpoint did not report", async () => {
    // 16384 is a guess. An administrator who cannot tell it from a measured value will not correct it,
    // and it silently sizes both the chat dial and the real context budget.
    open();
    await waitFor(() => expect(screen.getByText(/llama-3\.3-70b/)).toBeTruthy());

    const row = screen.getByText(/llama-3\.3-70b/).closest("li")!;
    expect(row.textContent).toMatch(/not reported|estimate/i);
  });

  it("imports exactly what is ticked", async () => {
    open();
    await waitFor(() => expect(screen.getByRole("checkbox", { name: /gpt-4o/ })).toBeTruthy());
    fireEvent.click(screen.getByRole("checkbox", { name: /llama-3\.3-70b/ }));   // untick it

    fireEvent.click(screen.getByRole("button", { name: /add 1 model/i }));

    await waitFor(() => expect(api.importModels).toHaveBeenCalled());
    expect((api.importModels as Mock).mock.calls[0][0].names).toEqual(["gpt-4o"]);
  });

  it("counts the ticked models in the confirm button", async () => {
    open();
    await waitFor(() => expect(screen.getByRole("button", { name: /add 2 models/i })).toBeTruthy());
  });

  it("shows an empty state rather than an enabled zero-model import", async () => {
    (api.discoverModels as Mock).mockResolvedValue([]);
    open();

    await waitFor(() => expect(screen.getByText(/no models/i)).toBeTruthy());
    expect(screen.queryByRole("button", { name: /add .* model/i })).toBeNull();
  });
});
```

Each found model must render in an `<li>` carrying its id, and each checkbox's accessible name must contain the model id, or the queries above will not resolve.

- [ ] **Step 2: Run, watch fail, implement**

Add an `Add all` button beside `Add model` in `LlmModels.tsx`, and the dialog component. Add `api.discoverModels` and `api.importModels`, plus the matching types. New i18n keys in all four locales, plain hyphens only.

- [ ] **Step 3: Run the web suite, mutation-verify, commit**

```bash
cd apps/web && npm test && npm run build
git add apps/web/src/components/llmmodels/DiscoverModelsDialog.tsx apps/web/src/components/llmmodels/DiscoverModelsDialog.test.tsx apps/web/src/pages/LlmModels.tsx apps/web/src/lib/api.ts apps/web/src/lib/types.ts apps/web/src/locales
git commit -m "feat: add all models from an endpoint"
```

---

### Task 18: Live verification and release (PR 2)

- [ ] **Step 1: Verify against the real LM Studio endpoint**

Point Add all at `http://192.168.1.129:1234/v1`. Confirm: the LM Studio path is taken (real context lengths appear, not 16,384 everywhere); embedding and whisper models are absent from the list; already-defined models are shown disabled; importing creates exactly the ticked rows with the right endpoint; imported rows are **not** ticked for chat.

- [ ] **Step 2: Verify the OpenAI-compatible fallback**

Point it at an endpoint with no `/api/v0/models` and confirm the fallback path imports at 16,384 with the shortfall flagged.

- [ ] **Step 3: Release bookkeeping**

Bump to `0.232.0` across `version.json` and its four mirrors; add the `RELEASES[0]` entry with the real PR number; update the README Features row and `docs/features.md` in lockstep; add the new outbound call and its bounds to `docs/Overall_Synopsis_of_Platform.md`; update `ai-model-settings.md`. **No `Data_Schema.md` edit** - PR 2 adds no schema.

- [ ] **Step 4: Full run, dash check, push, PR**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
cd apps/web && npm test && npm run build
git push -u origin feat/discover-models-from-endpoint
gh pr create --title "Add all models from an endpoint" --body-file /tmp/pr2-body.md
```

The PR body needs four headings:

- **What changed** - Add all, the two endpoints, and the non-LLM filter.
- **Deployment surface** - "Server redeploy only. No schema change."
- **Outbound fetch (please review)** - name the SSRF relaxation directly: this is the first endpoint that fetches an administrator-supplied URL, its sibling `POST /{id}/test` deliberately refuses one, and list the five bounds (ManagePlatform only, 10s timeout, no redirects, capped read, only parsed ids returned). A reviewer should see this named rather than have to find it.
- **Verified live / not verified** - what Steps 1 and 2 confirmed against the real LM Studio endpoint and the plain OpenAI-compatible fallback.
