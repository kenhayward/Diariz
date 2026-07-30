# Code and Coverage

A point-in-time snapshot of size, test volume and coverage across the four deployables plus the n8n node.

- **Version:** 0.174.0 - **commit:** `bd0aae55` - **captured:** 2026-07-30
- **Coverage source:** the `Coverage (report-only)` workflow run
  [30564908015](https://github.com/kenhayward/Diariz/actions/runs/30564908015) on `main`, at that same
  commit. Downloaded artifacts, not estimates.
- **Line counts:** physical lines including comments and blanks - the simple, reproducible measure.
  `node_modules`, `.venv`, `bin`, `obj` and `dist` are excluded entirely.

This is an internal reference. Regenerate it rather than editing the numbers by hand; it will drift.

---

## The table

| Component | Language | Code lines | Test lines | Test cases | Coverage (lines) |
|---|---|---:|---:|---:|---:|
| **API** | C# | 26,565 | 38,521 | 1,965 | **86.3%** |
| **Domain** | C# | 2,425 | *(shared)* | *(shared)* | **94.3%** |
| **Worker** | Python | 770 | 764 | 49 | **72.6%** † |
| **Web** | TypeScript / TSX | 36,346 | 19,768 | 1,632 | **84.8%** |
| **Desktop** | JavaScript | 1,770 | 868 | 99 | **99.8%** ‡ |
| **n8n node** | TypeScript | 4,156 | 908 | 98 | *none* |
| **Totals** | | **72,032** | **60,829** | **3,843** | |

Plus **114,978 lines of generated EF migrations** (73 migrations, 140 files) which are excluded from
every figure above. They are machine-written, never hand-edited, and would otherwise dwarf the entire
codebase - migrations alone are 1.6x all hand-written production code combined.

The API and Domain share one test estate and one coverage run, so their test columns are not split.
Of the 1,965 .NET cases, **1,742 are unit** (in-memory provider, no Docker) and **223 integration**
(real Postgres/pgvector, Redis and MinIO via Testcontainers). One unit test is deliberately skipped -
it asserts relational ordering the in-memory provider cannot reproduce, and lives for real in the
integration suite.

† **The worker's headline CI number is 87%, not 72.6%.** See the caveat below - 72.6% is the honest one.
‡ **Over a deliberately narrow denominator.** See the caveat below.

---

## Two coverage figures that need reading carefully

Both of the very high-looking numbers are measuring something narrower than they appear.

### The worker's 87% includes its own test files

`coverage.yml` runs `pytest --cov=.`, and `.` is the whole worker directory - so the seven test modules
(764 lines, all 100% covered by definition) land in the denominator alongside the production code.

Recomputed over production modules only:

| File | Statements | Missed | Covered |
|---|---:|---:|---:|
| `worker.py` | 130 | 27 | 79% |
| `pipeline.py` | 124 | 52 | **58%** |
| `callback.py` | 31 | 11 | 65% |
| `config.py` | 26 | 0 | 100% |
| `heartbeat.py` | 25 | 2 | 92% |
| `audio_merge.py` | 21 | 6 | 71% |
| `torch_compat.py` | 16 | 6 | 63% |
| `storage.py` | 15 | 0 | 100% |
| `healthcheck.py` | 3 | 3 | **0%** |
| **Production total** | **391** | **107** | **72.6%** |

`pipeline.py` at 58% is expected and largely irreducible: the WhisperX/pyannote/SpeechBrain model calls
need a GPU, and the suite stubs `whisperx` outright so it can run on CI without one. The pure transforms
were extracted into `_shape_segments` precisely so they *could* be tested, and they are. `healthcheck.py`
at 0% is three lines of Docker-invoked entry point.

**Worth fixing:** adding `--cov=. --cov-config` with an omit for `tests/*` would make the reported figure
mean what a reader assumes it means. The 14-point gap is presentational, not a real gap in testing.

### The desktop's 99.8% covers only the modules the tests load

Node's `--test --experimental-test-coverage` reports on files that were actually imported. The Electron
shell - `main.js`, `preload.js`, the setup/HTML windows - cannot run headless and is never loaded, so it
is absent from both numerator and denominator.

That absence is the point of the design (the pure logic was deliberately extracted into
`recorderState.js`, `updateState.js`, `hotkey.js`, `screenshotState.js`, `captureTarget.js`,
`desktopAuth.js`, `url.js` so it *could* be tested), but the number should be read as **"the extractable
logic is essentially fully covered"**, not "the desktop app is 99.8% covered". Roughly half of
`apps/desktop/src` by line count is shell that no test touches.

---

## Coverage in more depth

| Stack | Lines | Branches | Functions / Methods |
|---|---:|---:|---:|
| .NET (API + Domain) | 86.9% | 72.7% | 88.7% |
| Web (vitest v8) | 84.8% | 71.6% | 66.1% |
| Desktop (tested modules) | 99.8% | 94.8% | 98.8% |

The .NET figure merges the unit and integration runs, because much of the API is only reachable through
the integration suite - the in-memory EF provider cannot exercise relational ordering, FK enforcement or
the `vector(192)` cosine match. Generated migrations and the test assemblies are excluded via
`coverage.runsettings` and `-assemblyfilters`, so it is a production-only figure.

The web figure uses `--coverage.include='src/**'`, which counts **uncovered files too** - the honest
all-files number rather than only-what-was-touched. Its **functions at 66.1%** is the weakest headline
metric anywhere in the repo and is worth understanding: it largely reflects React components whose
handlers are defined but never invoked in a test (a rendered component with eight callbacks scores one
covered function out of nine). It is a symptom of render-only tests, not of untested logic.

**Coverage is deliberately non-gating.** The workflow is `continue-on-error` on every job and is kept out
of the required-checks list, so it can never block a merge. That is a considered choice: a coverage gate
mostly buys tests written to move a number.

---

## Test-to-code ratio

| Component | Test : code lines | Reading |
|---|---:|---|
| API + Domain | **1.33 : 1** | More test code than production code |
| Worker | 0.99 : 1 | Roughly parity |
| Web | 0.54 : 1 | About half |
| Desktop | 0.49 : 1 | About half, over a small base |
| n8n node | 0.22 : 1 | The thinnest |
| **Overall** | **0.84 : 1** | |

The .NET ratio being above 1:1 is a direct consequence of the TDD rule in `CLAUDE.md` plus a test estate
that carries real fixtures (`ContainersFixture`, `TestSupport` fakes) rather than mocking. The n8n node's
0.22:1 is the one that stands out, though it is somewhat mitigated: the node is largely **generated** from
the API's OpenAPI document, and its suite includes a snapshot test plus a cross-language signing-vector
test shared with `WebhookSignerFixtureTests.cs`, which is high-value per line.

The web ratio is the most interesting number in the table. 36k lines of TS/TSX against 20k of tests is
respectable, but combined with 66% function coverage it suggests the tests skew toward rendering and
away from interaction.

---

## Structural metrics

| Metric | Count |
|---|---:|
| Domain entities | 59 |
| EF migrations | 73 |
| API controllers | 43 |
| REST endpoints (`[Http*]` actions) | 226 |
| Test files, all stacks | 462 |
| Released versions | 319 |
| Help articles | 24 |
| Localisation catalogs | 49 files across 4 locales (5,235 lines) |
| Seeded prompts / formulas / meeting types | 19 files (441 lines) |
| Internal docs | 12 files (5,453 lines) |

226 endpoints across 43 controllers is dense for a platform at this stage, and reflects that the REST API
is a **product surface** (documented per-endpoint, consumed by the n8n node and MCP) rather than just
whatever the SPA happens to need.

---

## Where the gaps actually are

Ranked by what would change most per unit of effort:

1. **Web function coverage (66.1%).** The largest genuine gap. Handlers defined but never fired. Component
   tests that click, type and assert would move this and would catch real regressions - the kind of bug
   `RecordingsPanel.test.tsx` already demonstrates catching.
2. **The n8n node's 0.22:1 ratio and no coverage job at all.** It is published to npm under a version that
   cannot be corrected after the fact, which raises the cost of a mistake there above its line count.
3. **The worker's reported figure.** Not a testing gap - a reporting one. A `tests/*` omit would stop the
   number flattering itself by 14 points.
4. **The desktop shell.** `main.js` and friends are untested and untestable headless. The honest options
   are to extract more pure logic out of the shell (the existing pattern) or to accept it and say so.
   Recent live debugging of the shell has been done by attaching a CDP client to the packaged app, which
   works but is not a regression net.
5. **`pipeline.py` at 58%.** Mostly irreducible without a GPU in CI. Not worth chasing.

Nothing here is alarming. 86.9% on the API - the component holding auth, RBAC, ownership scoping and the
webhook signing path - is the number that matters most, and it is the one backed by a real-infrastructure
integration suite rather than mocks.

---

## Reproducing this

Coverage comes from the `Coverage (report-only)` workflow; download the `dotnet-coverage`,
`web-coverage` and `worker-coverage` artifacts from a run on `main`. Desktop coverage is printed in its
job log and to the run's Step Summary (it uploads no artifact). The n8n node has no coverage job.

Test counts:

```bash
dotnet test tests/Diariz.Api.Tests            # unit
dotnet test tests/Diariz.Api.IntegrationTests # integration (needs Docker)
cd apps/web && npm test
cd apps/desktop && npm test
cd integrations/n8n-nodes-diariz && npm test
cd src/Diariz.Worker && python -m pytest
```

Line counts were produced by walking the tree and counting physical lines per extension, excluding the
dependency and build directories listed at the top. Any equivalent tool (`cloc`, `tokei`) will give
slightly different figures depending on whether it discounts blanks and comments - the numbers here do
not.
