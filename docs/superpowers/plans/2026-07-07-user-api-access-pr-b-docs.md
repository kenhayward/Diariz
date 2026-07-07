# User API Access - PR B (Browsable API reference) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Publish a curated production OpenAPI document and an in-app, auth-gated Scalar API reference at `/developers/api`, and link to it from the Developers tab and the platform Integration tab.

**Architecture:** Reuse the existing `Microsoft.AspNetCore.OpenApi` generator: enable it in production but **curate** the document (only `api/*`, excluding `api/oauth*`; internal/OAuth/well-known/mcp are already non-`api/`) and declare the two bearer security schemes. Serve the JSON at `/api/openapi/v1.json` behind `[Authorize]` (the existing nginx `/api` proxy covers it). The reference itself is a React route that fetches the authed JSON (via the axios client, so the JWT rides along) and renders Scalar - so it's behind the SPA login, i.e. signed-in users only.

**Tech Stack:** ASP.NET Core 10 (`Microsoft.AspNetCore.OpenApi` 10.0.9 + `Microsoft.OpenApi` 2.7.5), React 19 + `@scalar/api-reference-react`.

**Curation rule:** include an operation iff its route (`ApiDescription.RelativePath`) starts with `api/` and does **not** start with `api/oauth`. Verified route inventory: user-facing controllers are all `api/*`; excluded are `api/oauth`, `api/oauth/connections`, `connect/register`, `internal/*`, `.well-known/*`, and `/mcp`.

**Verify-at-execution (API surface may differ slightly by version):**
- The exact `Microsoft.OpenApi` 2.7.5 types for a document transformer (`IOpenApiDocumentTransformer`, `OpenApiSecurityScheme`, `SecuritySchemeType.Http`, `OpenApiSecurityRequirement`). Confirm names compile; adjust if the 2.x API differs.
- The `@scalar/api-reference-react` export/props (expected: `ApiReferenceReact` with `configuration={{ content }}`). Confirm against the installed version.

---

## File map

**Create**
- `src/Diariz.Api/OpenApi/OpenApiCuration.cs` (pure include predicate + a document transformer)
- `tests/Diariz.Api.Tests/OpenApiCurationTests.cs`
- `apps/web/src/pages/ApiReference.tsx`
- `apps/web/src/pages/ApiReference.test.tsx`

**Modify**
- `src/Diariz.Api/Program.cs` (configure `AddOpenApi` with curation + transformer; move `MapOpenApi` to prod path + auth)
- `apps/web/src/lib/api.ts` (`getOpenApiDocument`)
- `apps/web/src/App.tsx` (auth-gated `/developers/api` route)
- `apps/web/src/components/DeveloperAccessSection.tsx` (+ test) - "View API reference" link
- `apps/web/src/components/SettingsModal.tsx` (+ test) - "View API reference" link on the Integration tab
- `apps/web/package.json` / `package-lock.json` (add `@scalar/api-reference-react`)
- Docs: `docs/Overall_Synopsis_of_Platform.md`, `apps/web/src/lib/releases.ts` (`CAPABILITIES` + `RELEASES[0]`)
- Version mirrors (-> `0.102.0`)

---

## Task 1: Curated, production OpenAPI document

**Files:**
- Create: `src/Diariz.Api/OpenApi/OpenApiCuration.cs`, `tests/Diariz.Api.Tests/OpenApiCurationTests.cs`
- Modify: `src/Diariz.Api/Program.cs`

- [ ] **Step 1: Write the failing test** (`OpenApiCurationTests.cs`) for the pure include predicate:

```csharp
using Diariz.Api.OpenApi;

namespace Diariz.Api.Tests;

public class OpenApiCurationTests
{
    [Theory]
    [InlineData("api/recordings", true)]
    [InlineData("api/user/api-tokens", true)]
    [InlineData("api/platform/settings", true)]
    [InlineData("api/oauth/connections", false)]  // OAuth plumbing excluded
    [InlineData("api/oauth", false)]
    [InlineData("internal/transcriptions/result", false)]
    [InlineData("connect/register", false)]
    [InlineData(".well-known/oauth-protected-resource", false)]
    [InlineData(null, false)]
    public void ShouldInclude_KeepsUserApiOnly(string? relativePath, bool expected) =>
        Assert.Equal(expected, OpenApiCuration.ShouldInclude(relativePath));
}
```

- [ ] **Step 2: Run it, verify it fails** (compile - `OpenApiCuration` missing).

Run: `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~OpenApiCurationTests`
Expected: FAIL.

- [ ] **Step 3: Implement `OpenApiCuration.cs`** - the pure predicate + a document transformer that adds the bearer security scheme(s) and a global requirement. (Confirm the `Microsoft.OpenApi` 2.7.5 type names compile; the shape below is the .NET 10 idiom.)

```csharp
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

namespace Diariz.Api.OpenApi;

/// <summary>Curation for the published OpenAPI document: only user-facing REST endpoints are included
/// (everything under <c>api/</c> except the OAuth plumbing under <c>api/oauth</c>); the worker callbacks
/// (<c>internal/*</c>), the OAuth server (<c>connect/*</c>, <c>.well-known/*</c>) and <c>/mcp</c> are dropped
/// because they are not <c>api/</c> routes. Also declares the bearer auth so the reference's "Authorize"
/// works with a personal API token or the session JWT.</summary>
public static class OpenApiCuration
{
    public static bool ShouldInclude(string? relativePath) =>
        relativePath is not null
        && relativePath.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
        && !relativePath.StartsWith("api/oauth", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds an HTTP bearer security scheme (a personal <c>dz_api_</c> token or the session JWT) and a
    /// global security requirement, so the reference UI can send an Authorization header.</summary>
    public sealed class SecuritySchemeTransformer : IOpenApiDocumentTransformer
    {
        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
        {
            var scheme = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                In = ParameterLocation.Header,
                Description = "A personal API token (dz_api_…) or the session JWT.",
            };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes["Bearer"] = scheme;
            document.SecurityRequirements.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Id = "Bearer", Type = ReferenceType.SecurityScheme } }] = []
            });
            document.Info.Title = "Diariz API";
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 4: Wire it in `Program.cs`.** Replace `builder.Services.AddOpenApi();` with:

```csharp
builder.Services.AddOpenApi("v1", options =>
{
    options.ShouldInclude = desc => Diariz.Api.OpenApi.OpenApiCuration.ShouldInclude(desc.RelativePath);
    options.AddDocumentTransformer<Diariz.Api.OpenApi.OpenApiCuration.SecuritySchemeTransformer>();
});
```

Replace the dev-only mapping:

```csharp
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
```

with a production, authenticated mapping under `/api` (so the existing nginx proxy covers it):

```csharp
app.MapOpenApi("/api/openapi/{documentName}.json").RequireAuthorization();
```

- [ ] **Step 5: Run tests + build**

Run: `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~OpenApiCurationTests` (PASS) then `dotnet build Diariz.slnx` (0 errors).
> If the `Microsoft.OpenApi` 2.7.5 API differs (e.g. `SecurityRequirements` naming, reference construction), adjust the transformer until it compiles; the `ShouldInclude` predicate + test are the stable contract.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/OpenApi/OpenApiCuration.cs tests/Diariz.Api.Tests/OpenApiCurationTests.cs src/Diariz.Api/Program.cs
git commit -m "feat(api-access): curated production OpenAPI document at /api/openapi/v1.json"
```

---

## Task 2: In-app Scalar API reference

**Files:**
- Modify: `apps/web/package.json` (dep), `apps/web/src/lib/api.ts`, `apps/web/src/App.tsx`
- Create: `apps/web/src/pages/ApiReference.tsx`, `apps/web/src/pages/ApiReference.test.tsx`

- [ ] **Step 1: Add the dependency**

Run: `cd apps/web && npm install @scalar/api-reference-react`
Expected: added to `package.json` + `package-lock.json`. Note the installed version and confirm the export is `ApiReferenceReact` (adjust the import in Step 4 if the package exposes a different name).

- [ ] **Step 2: Add the api client method** (`api.ts`), near the other GETs:

```typescript
  /// The curated OpenAPI document (authed) that backs the in-app API reference.
  async getOpenApiDocument(): Promise<unknown> {
    const { data } = await http.get("/api/openapi/v1.json");
    return data;
  },
```

- [ ] **Step 3: Write the failing test** (`ApiReference.test.tsx`) - mock the api + the Scalar component so it stays a unit test:

```tsx
import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({ api: { getOpenApiDocument: vi.fn() } }));
vi.mock("@scalar/api-reference-react", () => ({
  ApiReferenceReact: ({ configuration }: { configuration: { content: unknown } }) => (
    <div data-testid="scalar">{configuration.content ? "HAS_SPEC" : "NO_SPEC"}</div>
  ),
}));
import { api } from "../lib/api";
import ApiReference from "./ApiReference";

function renderIt() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><ApiReference /></QueryClientProvider>);
}

describe("ApiReference", () => {
  beforeEach(() => vi.clearAllMocks());
  it("fetches the OpenAPI document and renders Scalar with it", async () => {
    (api.getOpenApiDocument as ReturnType<typeof vi.fn>).mockResolvedValue({ openapi: "3.1.0", info: {} });
    renderIt();
    await waitFor(() => expect(api.getOpenApiDocument).toHaveBeenCalled());
    expect(await screen.findByText("HAS_SPEC")).toBeTruthy();
  });
});
```

- [ ] **Step 4: Run it, verify it fails** (page missing).

Run: `npx vitest run src/pages/ApiReference.test.tsx`
Expected: FAIL.

- [ ] **Step 5: Implement `ApiReference.tsx`** - a standalone full-page reference. (Confirm the import name against the installed package.)

```tsx
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { ApiReferenceReact } from "@scalar/api-reference-react";
import { api } from "../lib/api";

/// Full-page, in-app API reference (Scalar) backed by the curated, authed OpenAPI document. Reached at
/// /developers/api behind the app login, so it's signed-in users only.
export default function ApiReference() {
  const { t } = useTranslation("account");
  const { data: spec } = useQuery({ queryKey: ["openapi-doc"], queryFn: api.getOpenApiDocument });

  return (
    <div className="flex h-screen flex-col">
      <div className="flex items-center gap-3 border-b bg-white px-4 py-2 text-sm dark:border-gray-700 dark:bg-gray-900">
        <Link to="/" className="text-indigo-600 hover:underline dark:text-indigo-400">← {t("apiBackToApp")}</Link>
        <span className="font-medium text-gray-700 dark:text-gray-200">{t("apiReferenceTitle")}</span>
      </div>
      <div className="min-h-0 flex-1 overflow-auto">
        {spec && <ApiReferenceReact configuration={{ content: spec }} />}
      </div>
    </div>
  );
}
```

- [ ] **Step 6: Add i18n keys** `apiBackToApp` ("Back to app") and `apiReferenceTitle` ("Diariz API reference") to all four `account.json` (translate; plain hyphens).

- [ ] **Step 7: Add the auth-gated route** (`App.tsx`): import `ApiReference` and add, as a standalone full-page route (not inside `WorkspaceLayout`):

```tsx
      <Route path="/developers/api" element={<RequireAuth><ApiReference /></RequireAuth>} />
```

- [ ] **Step 8: Run tests + build**

Run: `npx vitest run src/pages/ApiReference.test.tsx src/locales.test.ts` (PASS) then `npm run build` (clean).

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/pages/ApiReference.tsx apps/web/src/pages/ApiReference.test.tsx apps/web/src/lib/api.ts apps/web/src/App.tsx apps/web/src/locales apps/web/package.json apps/web/package-lock.json
git commit -m "feat(api-access): in-app Scalar API reference at /developers/api"
```

---

## Task 3: Wire the "View API reference" links

**Files:** Modify `apps/web/src/components/DeveloperAccessSection.tsx` (+ test), `apps/web/src/components/SettingsModal.tsx` (+ test). The `apiViewReference` i18n key already exists (added in PR A).

- [ ] **Step 1: Developers tab link.** In `DeveloperAccessSection.tsx`, add a link to `/developers/api` near the base URL row (use react-router `Link`; opens the reference). Add `import { Link } from "react-router-dom";`.

```tsx
        <Link to="/developers/api" className={btn}>{t("apiViewReference")}</Link>
```

- [ ] **Step 2: Update the Developers test** - `DeveloperAccessSection.test.tsx` must wrap in a `MemoryRouter` (Link needs a router) and assert the reference link is present (`getByRole("link", { name: /view api reference/i })` with href `/developers/api`).

- [ ] **Step 3: Integration tab link.** In `SettingsModal.tsx`'s `integration` panel, add a link to `/developers/api` under the toggle. (SettingsModal already renders within the router.) Use `Link` (add the import if absent).

- [ ] **Step 4: Update the SettingsModal test** - extend the Integration test to assert the "View API reference" link is present with href `/developers/api`. (Confirm the test's render wraps in a router; add `MemoryRouter` if needed.)

- [ ] **Step 5: Run tests + build**

Run: `npx vitest run src/components/DeveloperAccessSection.test.tsx src/components/SettingsModal.test.tsx` (PASS) then `npm run build` (clean).

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/DeveloperAccessSection.tsx apps/web/src/components/DeveloperAccessSection.test.tsx apps/web/src/components/SettingsModal.tsx apps/web/src/components/SettingsModal.test.tsx
git commit -m "feat(api-access): link to the API reference from Developers + Integration"
```

---

## Task 4: Docs, version, release + verification

**Files:** `docs/Overall_Synopsis_of_Platform.md`, `apps/web/src/lib/releases.ts`, version mirrors.

- [ ] **Step 1: Update `Overall_Synopsis_of_Platform.md`** - a sentence in the API-access bullet (or a short subsection): the curated production OpenAPI doc at `/api/openapi/v1.json` (authed; only `api/*` minus `api/oauth`, with a bearer security scheme) and the in-app Scalar reference at `/developers/api` (signed-in users only), linked from the Developers + Integration tabs.

- [ ] **Step 2: Update `CAPABILITIES`** in `releases.ts` - extend the API-access sentence: a **browsable API reference** is available in-app when API access is enabled.

- [ ] **Step 3: Bump version to `0.102.0`** (functional enhancement) across `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, and regenerate both lockfiles.

- [ ] **Step 4: Add the `RELEASES[0]` entry** (`0.102.0`, the PR number, headline about the browsable API reference, `added` bullets).

- [ ] **Step 5: Full verification**

Run: `dotnet build Diariz.slnx` (0 errors); `dotnet test tests/Diariz.Api.Tests` (all pass); `cd apps/web && npm run build && npx vitest run` (pass, incl. `releases.test.ts`).

- [ ] **Step 6: Live verification** - rebuild the API container (`cd deploy && docker compose build api && docker compose up -d api`), enable API access, sign in to the SPA (dev server), open `/developers/api`, confirm the Scalar reference renders the curated endpoints (recordings/etc. present; `internal/*`, `connect/*`, `api/oauth` absent) and the Authorize control accepts a `dz_api_` token. Also `curl -H "Authorization: Bearer <jwt>" http://localhost:8080/api/openapi/v1.json` returns the JSON (and 401 without auth).

- [ ] **Step 7: Commit**

```bash
git add docs/Overall_Synopsis_of_Platform.md apps/web/src/lib/releases.ts version.json apps/web/package.json apps/web/package-lock.json apps/desktop/package.json apps/desktop/package-lock.json src/Diariz.Api/Diariz.Api.csproj
git commit -m "docs+release: browsable API reference (Scalar) 0.102.0"
```

---

## Deploy surface

Server redeploy (web + API). No migration, no worker rebuild, no desktop release. The OpenAPI JSON lives under `/api` so the existing nginx proxy covers it (no nginx change). Feature remains gated by `PlatformSettings.ApiAccessEnabled`.
