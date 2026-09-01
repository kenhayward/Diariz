# Contributing to Diariz

Thanks for your interest in contributing! This is a small, fast-moving project with a few firm
conventions. Please read these before opening a pull request.

## Licensing of contributions

By submitting a contribution you agree that it is licensed under the project's **AGPL-3.0** licence, and
that the maintainer may also offer it under the project's separate commercial licence (see the README).
If you can't agree to that, please don't submit a PR.

## Ground rules

### 1. Test-driven development (required)

Write the failing test first, watch it fail, then write the minimal code to pass. No production code
without a preceding failing test — this applies to features, bug fixes, and behaviour changes (when fixing
a bug, first add a test that reproduces it). Keep test output pristine: a passing run has **no errors or
warnings**.

The three stacks each have a harness:

- **.NET** — `dotnet test tests/Diariz.Api.Tests` (fast, no Docker) and
  `dotnet test tests/Diariz.Api.IntegrationTests` (Testcontainers — needs Docker).
- **Web** — `cd apps/web && npm test` (vitest).
- **Worker** — `cd src/Diariz.Worker && python -m pytest` (whisperx/torch are stubbed; no GPU needed).

There is no mocking library on the .NET side — add a fake to `Diariz.Api.TestSupport` instead.

### 2. One release per PR

Every PR ships exactly one release: **bump the version and add one release-notes entry.**

- Scheme is `Major.Minor.Build`. A **functional enhancement** bumps Minor +1 and resets Build to 0; any
  other PR (fix / chore / docs / refactor) bumps Build +1. Only bump Major when explicitly asked.
- The canonical version is [`version.json`](version.json). Bump it in lockstep with its **seven** mirrors:
  `apps/web/package.json`, `apps/desktop/package.json`, `integrations/n8n-nodes-diariz/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj` (`<Version>`), and all three `package-lock.json` files - web, desktop
  and n8n - each of which carries the version **twice**. `apps/web/src/lib/versionMirrors.test.ts` fails the
  build if any of them drifts.
- Add an entry to the top of `RECENT` in
  [`apps/web/src/lib/releaseNotes/current.ts`](apps/web/src/lib/releaseNotes/current.ts);
  `RECENT[0].version` **must equal** `version.json` (a test asserts this).
- **That is the only release-notes file to edit.** The rest of `lib/releaseNotes/` is a summarisation layer:
  older releases move to `archive.ts` under a named **epoch** in `epochs.ts`, and the release-notes page
  opens on those epochs with every entry still listed verbatim one click in. Closing an epoch is its own
  deliberate change, not part of shipping a release. `archive.ts` is loaded lazily by a single page and must
  not be imported anywhere else - a test asserts the module graph, because nothing else would catch it.

### 3. Keep the docs current

If a PR changes what the app does or its architecture/schema, update the matching docs in the **same PR**:
the README (Features / Architecture / Roadmap), `docs/Overall_Synopsis_of_Platform.md`, and
`docs/Data_Schema.md`. See [CLAUDE.md](CLAUDE.md) for the full rules.

### 4. State the deployment surface

In the PR description, say whether shipping the change needs a **desktop release** (only when it touches the
desktop shell in `apps/desktop`) or just a **server redeploy** (web/API changes). Docs/CI-only PRs need
neither — say so.

## Getting set up

See the [README](README.md) for per-stack build/run commands and the full-stack Docker Compose setup.

## Reporting security issues

Please don't file public issues for vulnerabilities — see [SECURITY.md](SECURITY.md).
