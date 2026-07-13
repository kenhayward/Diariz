# Formulas Phase A - Modal & icon polish (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Polish the Formulas modals and branding: a gradient-filled flask icon, flask icons on the modal
titles, stop the create/edit modal closing on an outside click, widen it with a one-row context picker and a
taller prompt, and re-badge/relayout the admin popup as "Manage Platform Formulas". Also fold in the deferred
About-box `CAPABILITIES` enrichment.

**Architecture:** Web-only (`apps/web`). No API, schema, or worker changes. Presentational + i18n changes,
plus the release bump. Deployment surface: **server redeploy (web)**, no desktop release.

**Tech Stack:** React 19 + TS + Vite + Tailwind v4; i18next (`en/de/es/fr`); vitest.

**Release:** Build bump `0.130.0` -> `0.130.1` (refinement, not a functional enhancement).

> **TDD note (needs human sign-off per CLAUDE.md):** Every change in this phase is presentational (SVG markup,
> Tailwind classes, i18n strings, modal open/close wiring) and no React component testing library is wired
> (`@testing-library/react` is not installed - see CLAUDE.md "Web" section). The one piece of testable logic
> - the version/release invariant - is already covered by `apps/web/src/lib/releases.test.ts` (asserts
> `RELEASES[0].version === version.json`), which is our red/green guard for the release bump. Remaining
> verification is `npm run build` (tsc typecheck) + the existing `npm test` suite + live browser preview
> screenshots. Wiring RTL for these cosmetic tweaks is out of scope; flag this exception for sign-off.

---

## Files

- Modify: `apps/web/src/components/FlaskIcon.tsx` - gradient liquid fill.
- Modify: `apps/web/src/components/FormulaEditModal.tsx` - no outside-close; title icon; wider; one-row
  context; taller prompt.
- Modify: `apps/web/src/components/FormulaRunModal.tsx` - title icon.
- Modify: `apps/web/src/components/ManageFormulasModal.tsx` - title icon; footer relayout (New bottom-left,
  Close bottom-right, shrunk).
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json` - `manageFormulas` -> "Manage Platform Formulas".
- Modify: `apps/web/src/locales/{en,de,es,fr}/admin.json` - `formulasTitle` -> "Manage Platform Formulas".
- Modify: `apps/web/src/lib/releases.ts` - new `RELEASES[0]` entry + enrich the `CAPABILITIES` Formulas & MCP
  rows.
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj` - version -> `0.130.1`.
- Modify (docs, lockstep): none required beyond `releases.ts` (this is a refinement; README/features/synopsis
  copy is unchanged - the CAPABILITIES enrichment just catches those rows up to the already-current README).

---

## Task A1: Gradient-filled flask icon

**Files:**
- Modify: `apps/web/src/components/FlaskIcon.tsx`

The icon is used at `currentColor` for its strokes in toolbars and titles. Add a **fill-only liquid sub-path**
with a **bright-blue gradient**, keeping all existing strokes `currentColor`. Use React 19 `useId()` for the
gradient id so multiple instances on one page don't collide.

- [ ] **Step 1: Replace the component**

```tsx
import { useId } from "react";
import { iconProps } from "./ToolbarButton";

/// The Formulas glyph (mirrors `images/formula-icon.svg`): a conical flask with a bright-blue liquid fill,
/// plus a sparkle to suggest AI-generated output. Feather-style stroke icon (strokes inherit `currentColor`
/// so it tints with its surroundings); only the liquid uses the blue gradient. `useId()` keeps the gradient
/// id unique when several icons render on one page.
export default function FlaskIcon() {
  const gid = useId();
  return (
    <svg {...iconProps}>
      <defs>
        <linearGradient id={gid} x1="0" y1="13" x2="0" y2="21" gradientUnits="userSpaceOnUse">
          <stop offset="0" stopColor="#38bdf8" />
          <stop offset="1" stopColor="#2563eb" />
        </linearGradient>
      </defs>
      {/* Liquid: fills the lower cone from the fill line (y=14) down through the rounded base. Fill only,
          no stroke, so it reads as liquid behind the outline. */}
      <path
        d="M6.6 14 L4.6 17.9 a2 2 0 0 0 1.7 3 h11.4 a2 2 0 0 0 1.7 -3 L17.4 14 Z"
        fill={`url(#${gid})`}
        stroke="none"
      />
      <path d="M9 2h6" />
      <path d="M10 2v6.5L4.5 18a2 2 0 0 0 1.7 3h11.6a2 2 0 0 0 1.7-3L14 8.5V2" />
      <path d="M6.5 14h11" />
      <path d="M19 3l.6 1.4L21 5l-1.4.6L19 7l-.6-1.4L17 5l1.4-.6z" />
    </svg>
  );
}
```

- [ ] **Step 2: Typecheck**

Run: `cd apps/web && npm run build`
Expected: PASS (tsc + vite build succeed).

- [ ] **Step 3: Visual verify (browser)**

Start the dev server (`preview_start` name `web`, or the project's launch config), open a page that shows the
icon (the transcript Formulas tab empty state renders `<FlaskIcon/>` large; the run picker shows it once Task
A3 lands). Screenshot and confirm: the liquid sits **inside** the flask below the `y=14` fill line, the blue
gradient reads at both toolbar (18px) and large sizes, and the strokes still tint with the heading colour in
light + dark mode. **If the liquid pokes outside the outline, nudge the `d` coordinates** (the flask base arc
is `4.5 18 -> +1.7,3 -> h11.6 -> +1.7,-3`; keep the liquid a hair inside).

- [ ] **Step 4: Commit**

```bash
git add apps/web/src/components/FlaskIcon.tsx
git commit -m "feat(formulas): add bright-blue gradient liquid to the flask icon"
```

---

## Task A2: FormulaEditModal - no outside-close, title icon, wider, one-row context, taller prompt

**Files:**
- Modify: `apps/web/src/components/FormulaEditModal.tsx`

- [ ] **Step 1: Import the icon**

Add to the imports at the top:

```tsx
import FlaskIcon from "./FlaskIcon";
```

- [ ] **Step 2: Stop closing on backdrop click; widen the dialog**

Change the backdrop `<div>` (currently `... onClick={onClose}>`) to drop the click handler, and remove the
now-pointless `stopPropagation` on the form. Also widen `max-w-lg` -> `max-w-4xl`.

Replace:
```tsx
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <form
        role="dialog"
        aria-label={title}
        className="flex max-h-[85vh] w-full max-w-lg flex-col space-y-3 overflow-y-auto rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
        onSubmit={save}
      >
        <h2 className="text-base font-semibold dark:text-gray-100">{title}</h2>
```
with:
```tsx
    {/* Does NOT close on a backdrop click (Escape or Cancel only) - avoids losing an in-progress edit. */}
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <form
        role="dialog"
        aria-label={title}
        className="flex max-h-[85vh] w-full max-w-4xl flex-col space-y-3 overflow-y-auto rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onSubmit={save}
      >
        <h2 className="flex items-center gap-2 text-base font-semibold dark:text-gray-100">
          <FlaskIcon />
          {title}
        </h2>
```

- [ ] **Step 3: Collapse the context checkboxes to one wrapping row + taller prompt**

Change the prompt textarea `rows={6}` -> `rows={12}` (uses the height reclaimed by the one-row context).

Replace the context grid:
```tsx
          <div className="grid grid-cols-2 gap-1.5">
```
with a wrapping single row:
```tsx
          <div className="flex flex-wrap gap-x-4 gap-y-1.5">
```

- [ ] **Step 4: Typecheck**

Run: `cd apps/web && npm run build`
Expected: PASS.

- [ ] **Step 5: Visual verify (browser)**

Open Preferences -> Formulas -> New formula (and Edit). Confirm: flask icon left of the title; clicking the
dark backdrop does **not** close it; Escape and Cancel do; the dialog is noticeably wider; the context
checkboxes sit on one wrapping line; the prompt box is taller. Screenshot.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/FormulaEditModal.tsx
git commit -m "feat(formulas): keep the edit modal open on outside click, widen it, one-row context, taller prompt, title icon"
```

---

## Task A3: FormulaRunModal - title icon

**Files:**
- Modify: `apps/web/src/components/FormulaRunModal.tsx`

- [ ] **Step 1: Import the icon** - add `import FlaskIcon from "./FlaskIcon";`

- [ ] **Step 2: Add the icon to the title**

Replace:
```tsx
        <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{t("workspace:formulaRunModalTitle")}</h2>
```
with:
```tsx
        <h2 className="mb-3 flex items-center gap-2 text-base font-semibold dark:text-gray-100">
          <FlaskIcon />
          {t("workspace:formulaRunModalTitle")}
        </h2>
```

- [ ] **Step 3: Typecheck** - `cd apps/web && npm run build` -> PASS.

- [ ] **Step 4: Visual verify** - open the transcript Formulas tab -> Run formula; confirm the flask icon
  sits left of "Run a formula". Screenshot.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/FormulaRunModal.tsx
git commit -m "feat(formulas): add the flask icon to the Run a formula title"
```

---

## Task A4: ManageFormulasModal - "Manage Platform Formulas" title icon + footer relayout

**Files:**
- Modify: `apps/web/src/components/ManageFormulasModal.tsx`

- [ ] **Step 1: Import the icon** - add `import FlaskIcon from "./FlaskIcon";`

- [ ] **Step 2: Icon on the title**

Replace:
```tsx
        <h2 className="mb-3 shrink-0 text-base font-semibold dark:text-gray-100">{t("formulasTitle")}</h2>

        <div className="mb-3 shrink-0">
          <button
            type="button"
            onClick={() => setEditing(null)}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white dark:bg-gray-100 dark:text-gray-900"
          >
            {t("account:newFormula")}
          </button>
        </div>

```
with (drop the top New-formula block - it moves to the footer):
```tsx
        <h2 className="mb-3 flex shrink-0 items-center gap-2 text-base font-semibold dark:text-gray-100">
          <FlaskIcon />
          {t("formulasTitle")}
        </h2>

```

- [ ] **Step 3: Footer: New bottom-left, shrunk Close bottom-right**

Replace the footer:
```tsx
        <div className="mt-3 flex shrink-0 border-t pt-3 dark:border-gray-700">
          <button
            onClick={onClose}
            className="w-full rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:close")}
          </button>
        </div>
```
with:
```tsx
        <div className="mt-3 flex shrink-0 items-center justify-between border-t pt-3 dark:border-gray-700">
          <button
            type="button"
            onClick={() => setEditing(null)}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white dark:bg-gray-100 dark:text-gray-900"
          >
            {t("account:newFormula")}
          </button>
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:close")}
          </button>
        </div>
```

- [ ] **Step 4: Typecheck** - `cd apps/web && npm run build` -> PASS.

- [ ] **Step 5: Visual verify** - open the account menu -> "Manage Platform Formulas" (label changes in Task
  A5); confirm the flask icon on the title, New at bottom-left, a normal-width Close at bottom-right, and that
  the backdrop click still does not dismiss it. Screenshot.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/ManageFormulasModal.tsx
git commit -m "feat(formulas): title icon + footer relayout (New bottom-left, Close bottom-right) for the platform formulas modal"
```

---

## Task A5: Rename to "Manage Platform Formulas" (i18n, all locales)

**Files:**
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json` (`manageFormulas`)
- Modify: `apps/web/src/locales/{en,de,es,fr}/admin.json` (`formulasTitle`)

The account-menu item (`account.manageFormulas`) and the modal title (`admin.formulasTitle`) both become
"Manage Platform Formulas" (and locale equivalents). Keep the established translation of "Formula(s)"
(de `Formeln`, es `fórmulas`, fr `formules`). No em/en dashes.

- [ ] **Step 1: Set both keys per locale**

| Locale | `account.manageFormulas` & `admin.formulasTitle` |
| --- | --- |
| en | `Manage Platform Formulas` |
| de | `Plattform-Formeln verwalten` |
| es | `Gestionar fórmulas de la plataforma` |
| fr | `Gérer les formules de la plateforme` |

Edit each of the 8 files to set the respective key to the value above. (Both keys get the same string within
a locale.)

- [ ] **Step 2: Verify JSON parses + strings resolve**

Run:
```bash
node -e "for(const l of ['en','de','es','fr']){const a=require('./apps/web/src/locales/'+l+'/account.json'),d=require('./apps/web/src/locales/'+l+'/admin.json');console.log(l,JSON.stringify([a.manageFormulas,d.formulasTitle]));}"
```
Expected: each locale prints the two matching strings above (no parse error).

- [ ] **Step 3: Typecheck** - `cd apps/web && npm run build` -> PASS.

- [ ] **Step 4: Commit**

```bash
git add apps/web/src/locales
git commit -m "i18n(formulas): rename to Manage Platform Formulas across locales"
```

---

## Task A6: About-box CAPABILITIES enrichment (deferred docs tweak)

**Files:**
- Modify: `apps/web/src/lib/releases.ts` (the `CAPABILITIES` template string)

Bring the Formulas and MCP capability rows up to the README's wording (three scopes, `/formula` in chat,
Claude via MCP `run_formula`, admin management). No em/en dashes.

- [ ] **Step 1: Replace the Formulas row**

Replace:
```
| **Formulas** | Save a prompt + a context and run it over a recording to generate a Markdown document you can edit, download, or email. |
```
with:
```
| **Formulas** | Save a prompt + a context and run it over a recording (personal, platform-wide, or built-in) to generate a Markdown document you can edit, download, or email - from the Formulas tab, \`/formula\` in chat, or Claude via MCP; admins manage the shared ones. |
```

- [ ] **Step 2: Replace the MCP row**

Replace:
```
| **Connect Claude (MCP)** | Connect Claude to your own meetings via OAuth (claude.ai) or a personal token (Claude Desktop/Code). |
```
with:
```
| **Connect Claude (MCP)** | Connect Claude to your own meetings via OAuth (claude.ai) or a personal token (Claude Desktop/Code), including a \`run_formula\` tool to run your saved Formulas. |
```

(Note the backtick escaping - `CAPABILITIES` is a JS template literal, so inline-code backticks must be
`\``.)

- [ ] **Step 3: Verify the template still compiles** - `cd apps/web && npm run build` -> PASS (a stray
  unescaped backtick would break the build).

- [ ] **Step 4: Commit**

```bash
git add apps/web/src/lib/releases.ts
git commit -m "docs(about): enrich the Formulas and MCP capability rows to match the README"
```

---

## Task A7: Version bump + release entry

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj`
- Modify: `apps/web/src/lib/releases.ts` (prepend `RELEASES[0]`)

Bump `0.130.0` -> `0.130.1` (Build +1; this is a refinement, not a functional enhancement).

- [ ] **Step 1: Write a failing check first (TDD guard)**

Bump only `version.json` to `0.130.1` and run the web tests:
Run: `cd apps/web && npm test -- releases`
Expected: FAIL - `releases.test.ts` asserts `RELEASES[0].version === version.json` (now mismatched). This is
our red state.

- [ ] **Step 2: Bump the three mirrors + prepend the release entry**

Set `<Version>` in `src/Diariz.Api/Diariz.Api.csproj`, and `"version"` in `apps/web/package.json` +
`apps/desktop/package.json`, to `0.130.1`. Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`:

```ts
  {
    version: "0.130.1",
    date: "2026-07-13",
    pr: 0, // set to the real PR number after opening the PR
    headline: "Formulas polish: gradient flask, tidier modals",
    summary:
      "Refines the Formulas modals and branding. The flask icon now has a bright-blue liquid, and it appears beside the New Formula, Edit Formula, and Run a formula titles. The create/edit modal no longer closes if you click outside it (so you can't lose an in-progress prompt), and it is wider with a single-row context picker and a taller prompt box. The admin popup is renamed Manage Platform Formulas, with New moved to the bottom-left and a tidier Close at the bottom-right. The About box now describes Formulas' scopes, the /formula chat command, and the run_formula MCP tool.",
    changed: [
      "Flask icon: bright-blue gradient liquid; shown on the New Formula, Edit Formula, and Run a formula titles.",
      "Create/edit formula modal no longer closes on an outside click; it is wider, with a one-row context picker and a taller prompt.",
      "Admin popup renamed Manage Platform Formulas; New formula moved to the bottom-left, Close to the bottom-right.",
      "About box: enriched Formulas and Connect Claude (MCP) descriptions.",
    ],
  },
```

- [ ] **Step 3: Tests go green**

Run: `cd apps/web && npm test`
Expected: PASS (releases invariant satisfied; whole suite green, no new warnings).

- [ ] **Step 4: Full web build** - `cd apps/web && npm run build` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts
git commit -m "chore(release): 0.130.1 - Formulas polish"
```

> Leave `pr: 0` until the PR exists, then edit it to the real number and amend/append a fixup commit before
> merge (matches the repo's convention).

---

## Finish

- [ ] Run the full web suite once more: `cd apps/web && npm test` and `npm run build` - both green, output
  pristine.
- [ ] Use **superpowers:finishing-a-development-branch**: push `feat/formulas-phase-a-polish` and open a PR.
  PR body: state the deployment surface = **server redeploy (web)**, **no desktop release**; note the TDD
  carve-off (presentational; RTL not wired) for the reviewer.
- [ ] After the PR number is known, set `RELEASES[0].pr` and the release entry's `pr` to it.

## Self-review checklist (run before dispatching)
- Spec coverage: A1 gradient / A2 edit-modal (no-close, icon, wide, one-row, tall) / A3 run-modal icon /
  A4+A5 Manage Platform Formulas (icon, footer, rename) / A6 CAPABILITIES / A7 release - all Phase-A spec
  bullets mapped. ✓
- No placeholders: every step shows the exact code or exact strings. ✓
- Consistency: `FlaskIcon` default export imported the same way in A2/A3/A4; `max-w-4xl` matches the admin
  modal's existing width; `pr: 0` sentinel called out. ✓
