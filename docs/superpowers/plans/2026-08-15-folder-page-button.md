# Folder-Page Button Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote "open this folder's page" out of the list-view breadcrumb's dropdown into a permanent page-icon button immediately left of the dropdown chevron, and remove every user-visible use of the word "section" that means *folder*.

**Architecture:** `FolderPath` owns the chevron trigger, so the only way to sit "immediately left of the dropdown" is a slot inside it. It gains one optional `trailingAction?: ReactNode` prop rendered between its `<nav>` and its chevron `<button>`; `DrillBreadcrumb` fills that slot with a react-router `<Link>` to the same destination the menu entry used. `DrillBreadcrumb` is the only consumer of `FolderPath`'s `extraItems` prop, so that prop is deleted outright and the dropdown becomes purely the ancestor chain. The rest of the work is copy: locale catalogs, help articles, About-box capabilities, README/feature docs, and three raw-English API error strings.

**Tech Stack:** React 19 + TypeScript + Vite + Tailwind v4, react-i18next (4 locales: en/de/es/fr), react-router-dom, Vitest + @testing-library/react (jsdom), ASP.NET Core (C#) for the API strings.

**Spec:** `docs/superpowers/specs/2026-08-15-folder-page-button-design.md`

## Global Constraints

- **Branch:** work on `feat/folder-page-button` (already created, spec already committed). `main` is branch-protected - never commit or push to it. Finish by pushing and opening a PR.
- **TDD is mandatory.** Write the failing test, run it, watch it fail with the expected message, then write the minimal code. No production code without a preceding red test.
- **No `@testing-library/jest-dom`.** It is not a dependency of this repo and must not be added. Use plain assertions (`expect(x).toBeTruthy()`, `expect(x.getAttribute("href")).toBe(...)`).
- **Never `git add -A` or `git add .`** in this repo - it sweeps hundreds of untracked agent scratch files into the commit. Stage explicit paths only.
- **No em/en dashes (`—` / `–`) in user-facing text.** Plain hyphen `-` only. Applies to UI strings, all locale catalogs, help articles, release notes and README.
- **Help articles are ASCII only** (`apps/web/src/content/help/**`), enforced by `helpContent.test.ts`.
- **Code, routes, DTOs, C# types and SQL keep the word "Section".** `SectionsController`, `/api/sections`, `sectionId`, `SectionDetail.tsx`, i18n *key* names - all unchanged. Only user-visible *values* change.
- **Do not touch the other meaning of "section":** minutes/formula template sections ("Add section", "Section title", "Every section needs a title.", "Per section (best structure)"), page-area references ("Calendar Event section", "Notes-tab section"), API-reference endpoint groups, and historical `RELEASES` entries.
- **Version:** 0.213.0 -> **0.214.0** (functional enhancement: Minor +1, Build reset to 0).
- **Deployment surface:** server redeploy only. No desktop release (nothing under `apps/desktop/src/**`, `apps/desktop/build/**` or `electron-builder.config.js` changes; the lockstep bump to `apps/desktop/package.json` does not by itself require one).
- Run web tests from `apps/web` with `npm test -- <path>` (the `test` script is `vitest run`).

---

### Task 1: The folder-page button

Replaces the dropdown's "Open section page >" entry with a `<Link>` button in the breadcrumb row.

**Files:**
- Modify: `apps/web/src/components/nav/FolderPath.tsx` (remove `extraItems`, add `trailingAction`)
- Modify: `apps/web/src/components/nav/DrillBreadcrumb.tsx:63-78`
- Modify: `apps/web/src/locales/en/workspace.json:448`, `de/workspace.json:448`, `es/workspace.json:448`, `fr/workspace.json:448`
- Test: `apps/web/src/components/nav/FolderPath.test.tsx`
- Test: `apps/web/src/components/nav/DrillBreadcrumb.test.tsx`
- Test: `apps/web/src/components/RecordingsPanel.test.tsx:343-352`

**Interfaces:**
- Consumes: `FileTextIcon` from `apps/web/src/components/icons.tsx` (signature `(p: IconProps) => JSX.Element`, `IconProps = { size?: number; title?: string }`); `useDrillSearch()` from `apps/web/src/lib/drillRoute.ts` returning the string `"?in=<id>"`; the `basePath` prop already passed to `DrillBreadcrumb` by `RecordingsPanel.tsx:319`.
- Produces: `FolderPath`'s new prop `trailingAction?: ReactNode`. `FolderPath`'s `extraItems` prop **ceases to exist** - no later task may reference it. New i18n key `workspace:drillOpenFolderPage` (English value `View folder page`), replacing `workspace:drillOpenSectionPage`, which ceases to exist.

- [ ] **Step 1: Rename the i18n key in all four locales**

This is the string the new tests assert on, so it lands before them. In each of `apps/web/src/locales/{en,de,es,fr}/workspace.json`, line 448, replace the `drillOpenSectionPage` entry. Note the trailing `›` is dropped - it signalled "menu entry that navigates" and this is now a tooltip.

```
en: "drillOpenFolderPage": "View folder page",
de: "drillOpenFolderPage": "Ordnerseite anzeigen",
es: "drillOpenFolderPage": "Ver página de la carpeta",
fr: "drillOpenFolderPage": "Voir la page du dossier",
```

- [ ] **Step 2: Write the failing FolderPath tests**

In `apps/web/src/components/nav/FolderPath.test.tsx`, **delete** the two `extraItems` tests - `"puts extra items at the top of the menu"` (lines 62-70) and `"renders an extra item carrying \`to\` as a link rather than a button"` (lines 91-107) - and add:

```tsx
  // The slot exists so a caller can put a control immediately left of the menu trigger. Order is the
  // whole point of the prop, so it is asserted as DOM order, not merely presence.
  it("renders trailingAction between the path and the menu trigger", () => {
    const { container } = render(
      <FolderPath crumbs={crumbs} trailingAction={<button type="button">Page</button>} />,
    );

    const children = Array.from(container.firstElementChild!.children);
    expect(children.indexOf(screen.getByRole("navigation"))).toBe(0);
    expect(children.indexOf(screen.getByRole("button", { name: "Page" }))).toBe(1);
    expect(children.indexOf(screen.getByLabelText("Show full folder path"))).toBe(2);
  });

  it("renders no trailing slot when no action is given", () => {
    const { container } = render(<FolderPath crumbs={crumbs} />);

    // Just the nav and the menu trigger - an empty slot must not leave a stray element behind.
    expect(container.firstElementChild!.children.length).toBe(2);
  });
```

If deleting the `extraItems` tests leaves `MemoryRouter` unused in this file's imports, remove it from the import - an unused import fails the build's `tsc` pass.

- [ ] **Step 3: Run the FolderPath tests to verify they fail**

```bash
cd apps/web && npm test -- src/components/nav/FolderPath.test.tsx
```

Expected: FAIL. `"renders trailingAction between the path and the menu trigger"` fails because `trailingAction` is not a prop, so nothing renders and `screen.getByRole("button", { name: "Page" })` throws "Unable to find an accessible element". (`"renders no trailing slot"` will already pass - that is fine, it is a regression guard.)

- [ ] **Step 4: Implement the FolderPath change**

In `apps/web/src/components/nav/FolderPath.tsx`:

Add the `ReactNode` type import to line 1:
```tsx
import { useEffect, useRef, useState, type ReactNode } from "react";
```

Remove `extraItems = [],` from the destructured props (line 23) and delete its type entry with its doc comment (lines 33-36). Add in their place:

```tsx
  /// A control rendered between the path and the menu trigger - the nav puts the folder-page button
  /// here. Kept as an opaque node so this component stays presentational: it places the control, and
  /// knows nothing about where it goes.
  trailingAction?: ReactNode;
```
and add `trailingAction,` to the destructure.

Render the slot immediately before the chevron `<button>` (between the closing `</nav>` on line 127 and the button on line 129):

```tsx
      {trailingAction}
```

Delete the `extraItems.map(...)` block (lines 145-170) and the divider on line 171, leaving the menu as just the ancestor-chain `crumbs.map(...)`. If `Link` is now unused in this file, remove it from the `react-router-dom` import.

Update the class comment on lines 16-18 so it still describes reality:

```tsx
/// The **trailing chevron** is the menu trigger and is always present, so the full hierarchy is one click
/// away whether or not the path is collapsed. The collapsed `…` is a plain indicator, not a second trigger:
/// two controls opening the same menu is not affordable in a strip this narrow. Anything a caller wants
/// beside the trigger goes in `trailingAction`, which sits between the path and the chevron.
```

- [ ] **Step 5: Run the FolderPath tests to verify they pass**

```bash
cd apps/web && npm test -- src/components/nav/FolderPath.test.tsx
```
Expected: PASS, all tests in the file.

- [ ] **Step 6: Write the failing DrillBreadcrumb tests**

In `apps/web/src/components/nav/DrillBreadcrumb.test.tsx`, replace the three menu-driven tests (lines 103-153: `"opens the folder's page from the menu, not a drill"`, `"keeps the drill position when opening the folder page"`, `"keeps the room prefix on the section page link in a shared room"`) with:

```tsx
  // The design's two distinct targets: a crumb browses deeper, this button opens the page. It is a
  // button in the row now, not an entry buried in the menu.
  it("opens the folder's page from the button, not a drill", async () => {
    const onDrill = vi.fn();
    let location = { pathname: "", search: "" };
    render(
      <MemoryRouter initialEntries={["/?in=ambu"]}>
        <LocationSpy onChange={(loc) => (location = loc)} />
        <DrillBreadcrumb sections={sections} sectionId="ambu" basePath="" onDrill={onDrill} />
      </MemoryRouter>,
    );

    await userEvent.click(screen.getByRole("link", { name: "View folder page" }));

    expect(location.pathname).toBe("/sections/ambu");
    expect(onDrill).not.toHaveBeenCalled();
  });

  // Opening the page must not throw away where you were browsing: the drill lives in ?in=, and a bare
  // navigate to "/sections/:id" drops the query, popping the panel back to the root behind the page you
  // opened.
  it("keeps the drill position when opening the folder page", async () => {
    let location = { pathname: "", search: "" };
    render(
      <MemoryRouter initialEntries={["/?in=ambu"]}>
        <LocationSpy onChange={(loc) => (location = loc)} />
        <DrillBreadcrumb sections={sections} sectionId="ambu" basePath="" onDrill={vi.fn()} />
      </MemoryRouter>,
    );

    await userEvent.click(screen.getByRole("link", { name: "View folder page" }));

    expect(location.pathname + location.search).toBe("/sections/ambu?in=ambu");
  });

  it("keeps the room prefix on the folder page button in a shared room", () => {
    render(
      <MemoryRouter initialEntries={["/?in=ambu"]}>
        <DrillBreadcrumb sections={sections} sectionId="ambu" basePath="/rooms/r1" onDrill={vi.fn()} />
      </MemoryRouter>,
    );

    const link = screen.getByRole("link", { name: "View folder page" });
    expect(link.getAttribute("href")).toBe("/rooms/r1/sections/ambu?in=ambu");
  });

  // Promoting the button out of the menu means taking it OUT of the menu - one action, one control.
  it("leaves the menu as nothing but the ancestor chain", async () => {
    renderCrumb("ambu");

    await userEvent.click(screen.getByLabelText("Show full folder path"));

    const items = screen.getAllByRole("menuitem").map((el) => el.textContent);
    expect(items).toEqual(["Customers", "Ambu"]);
  });
```

Then extend two existing tests so the button's absence rules are pinned to this feature rather than inherited by accident.

In `"renders nothing at the root"` (line 32), after the existing `expect(container.innerHTML).toBe("")`:
```tsx
    // The dropdown does not exist at the root, so neither may the button.
    expect(screen.queryByRole("link", { name: "View folder page" })).toBeNull();
```

In `"still offers a way out for an unknown folder"` (line 156), before the existing `fireEvent.click`:
```tsx
    // The folder was deleted underneath us - offer the way out, not a link to a page that is gone.
    expect(screen.queryByRole("link", { name: "View folder page" })).toBeNull();
```

- [ ] **Step 7: Run the DrillBreadcrumb tests to verify they fail**

```bash
cd apps/web && npm test -- src/components/nav/DrillBreadcrumb.test.tsx
```

Expected: FAIL. The three button tests fail with "Unable to find an accessible element with the role \"link\" and name \"View folder page\"" (no such control exists yet). `"leaves the menu as nothing but the ancestor chain"` fails because the menu still holds the old entry, so the received array is `["View folder page", "Customers", "Ambu"]` - note the label already reads "View folder page" after Step 1, which confirms the key rename took effect.

- [ ] **Step 8: Implement the DrillBreadcrumb change**

In `apps/web/src/components/nav/DrillBreadcrumb.tsx`, add the imports:
```tsx
import { Link } from "react-router-dom";
import { ArrowLeftIcon, FileTextIcon, FolderIcon } from "../icons";
```

Replace the `extraItems={...}` prop (lines 68-77) with:

```tsx
        trailingAction={
          current ? (
            <Link
              to={{ pathname: `${basePath}/sections/${current.id}`, search: drillSearch }}
              title={t("drillOpenFolderPage")}
              aria-label={t("drillOpenFolderPage")}
              className="shrink-0 rounded p-0.5 text-gray-400 hover:bg-gray-100 hover:text-gray-700 dark:text-gray-500 dark:hover:bg-gray-800 dark:hover:text-gray-200"
            >
              <FileTextIcon size={14} />
            </Link>
          ) : undefined
        }
```

A `Link` (not a `button`) so ctrl-click, middle-click and "open in new tab" keep working, as they did for the menu entry. Both `title` and `aria-label` per the `ToolbarButton` convention: `title` gives the hover tooltip, `aria-label` the accessible name. The button is never disabled, so it needs no wrapper span (contrast `ListToolbar.tsx:172-174`, where `disabled:pointer-events-none` swallows the tooltip).

Update the two comments that describe the old placement. The class comment on lines 9-14:

```tsx
/// The drill-in list's header row: a back button, the folder path, a button opening the folder's own page,
/// and a menu carrying the full ancestor chain.
///
/// Clicking a crumb and the folder-page button are deliberately **distinct targets** and must stay that way:
/// a crumb browses to that level (`onDrill`), while the button navigates the middle panel to the folder
/// itself. Collapsing them would make it impossible to reach a folder's page once you had drilled in.
```

and the inline comment on lines 60-62:

```tsx
      {/* Clicking a crumb DRILLS to that level; the trailing button navigates the middle panel to the
          folder's own page. Those stay distinct targets - collapsing them would make a folder's page
          unreachable once you had drilled into it. The page link carries `?in=` so opening it does not
          pop the list home. */}
```

- [ ] **Step 9: Run the DrillBreadcrumb tests to verify they pass**

```bash
cd apps/web && npm test -- src/components/nav/DrillBreadcrumb.test.tsx
```
Expected: PASS, all tests in the file.

- [ ] **Step 10: Update the RecordingsPanel integration test and run it**

In `apps/web/src/components/RecordingsPanel.test.tsx`, replace the test at lines 343-352 with:

```tsx
    // The two targets the design insists stay distinct: the row browses, the button opens the page. It
    // keeps `?in=` so opening the page leaves you where you were browsing.
    it("opens the folder page from the breadcrumb button, not by drilling", async () => {
      let location = { pathname: "", search: "" };
      renderListWithLocationSpy("/?in=customers", (loc) => (location = loc));
      fireEvent.click(await screen.findByRole("link", { name: "View folder page" }));
      expect(location.pathname).toBe("/sections/customers");
      expect(location.search).toBe("?in=customers");
    });
```

```bash
cd apps/web && npm test -- src/components/RecordingsPanel.test.tsx
```
Expected: PASS.

- [ ] **Step 11: Run the whole web suite and the typecheck**

```bash
cd apps/web && npm test
```
Expected: PASS. Any other test that referenced the old menu entry surfaces here - fix it the same way (click the link by its accessible name).

```bash
cd apps/web && npm run build
```
Expected: clean `tsc` then a successful vite build. This is what catches an unused `Link`/`MemoryRouter` import left behind by Steps 2 and 4.

- [ ] **Step 12: Commit**

```bash
git add apps/web/src/components/nav/FolderPath.tsx apps/web/src/components/nav/FolderPath.test.tsx apps/web/src/components/nav/DrillBreadcrumb.tsx apps/web/src/components/nav/DrillBreadcrumb.test.tsx apps/web/src/components/RecordingsPanel.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(nav): promote the folder page to a button beside the breadcrumb menu"
```

---

### Task 2: Locale wording sweep

Every remaining user-visible string where "section" means *folder*, across four locales.

**Files:**
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`
- Modify: `apps/web/src/locales/{en,de,es,fr}/recordings.json`
- Modify: `apps/web/src/locales/{en,de,es,fr}/tour.json`

**Interfaces:**
- Consumes: nothing from Task 1 beyond the already-renamed `drillOpenFolderPage` key (do not touch it again).
- Produces: no new keys. **Key names are unchanged** - only values. Later tasks rely on the English UI reading "folder": specifically `searchFilterSection` = `Folder`, `moveToSectionTitle` = `Move to folder`, `moveToSection` = `Move to folder…`.

- [ ] **Step 1: Rewrite the English values**

`apps/web/src/locales/en/workspace.json`:
```
line 420  "newSectionPlaceholder": "New folder name",
line 421  "newSection": "New folder",
line 472  "confirmDeleteSection": "Delete folder \"{{name}}\"? Its recordings move to Ungrouped.",
line 473  "sectionActions": "Folder actions",
line 474  "sectionNameAria": "Folder name",
line 476  "newSubSection": "New sub-folder",
line 477  "newSubSectionPlaceholder": "New sub-folder in {{parent}}",
line 531  "moveToSectionTitle": "Move to folder",
line 668  "searchFilterSection": "Folder",
line 673  "roomCounts_one": "{{count}} folder",
line 674  "roomCounts_other": "{{count}} folders",
```
(`newSectionNestCapped` on line 475 already reads "Folders can only be nested 8 levels deep" - leave it.)

`apps/web/src/locales/en/recordings.json`:
```
line 11  "moveToSection": "Move to folder…",
line 14  "moveToSectionShort": "Move to folder",
```

`apps/web/src/locales/en/tour.json` line 13:
```
"body": "Recordings appear here as they finish. Organise them into folders, drag to reorder, and drop audio files anywhere on this panel to upload."
```

- [ ] **Step 2: Rewrite the German values**

`apps/web/src/locales/de/workspace.json` - *Abschnitt* -> *Ordner*, *Unterabschnitt* -> *Unterordner*:
```
line 420  "newSectionPlaceholder": "Name des neuen Ordners",
line 421  "newSection": "Neuer Ordner",
line 446  "ungrouped": "Ohne Ordner",
line 472  "confirmDeleteSection": "Ordner „{{name}}“ löschen? Seine Aufnahmen werden nach „Ohne Ordner“ verschoben.",
line 473  "sectionActions": "Ordneraktionen",
line 474  "sectionNameAria": "Ordnername",
line 475  "newSectionNestCapped": "Ordner können nur 8 Ebenen tief verschachtelt werden",
line 476  "newSubSection": "Neuer Unterordner",
line 477  "newSubSectionPlaceholder": "Neuer Unterordner in {{parent}}",
line 531  "moveToSectionTitle": "In Ordner verschieben",
line 668  "searchFilterSection": "Ordner",
line 673  "roomCounts_one": "{{count}} Ordner",
line 674  "roomCounts_other": "{{count}} Ordner",
```
`line 446` and `line 472` must move together - the delete confirmation quotes the Ungrouped label by name.

`de/recordings.json`: line 11 `"In Ordner verschieben…"`, line 14 `"In Ordner verschieben"`.
`de/tour.json` line 13: `"Aufnahmen erscheinen hier, sobald sie fertig sind. Ordne sie in Ordner, ziehe sie zum Umordnen und lege Audiodateien irgendwo auf diesem Panel ab, um sie hochzuladen."`

- [ ] **Step 3: Rewrite the Spanish values**

`apps/web/src/locales/es/workspace.json` - *sección* -> *carpeta*, *subsección* -> *subcarpeta*:
```
line 420  "newSectionPlaceholder": "Nombre de la nueva carpeta",
line 421  "newSection": "Nueva carpeta",
line 472  "confirmDeleteSection": "¿Eliminar la carpeta \"{{name}}\"? Sus grabaciones pasarán a Sin agrupar.",
line 473  "sectionActions": "Acciones de la carpeta",
line 474  "sectionNameAria": "Nombre de la carpeta",
line 475  "newSectionNestCapped": "Las carpetas solo pueden anidarse hasta 8 niveles de profundidad",
line 476  "newSubSection": "Nueva subcarpeta",
line 477  "newSubSectionPlaceholder": "Nueva subcarpeta en {{parent}}",
line 531  "moveToSectionTitle": "Mover a carpeta",
line 668  "searchFilterSection": "Carpeta",
line 673  "roomCounts_one": "{{count}} carpeta",
line 674  "roomCounts_other": "{{count}} carpetas",
```
(`ungrouped` is already "Sin agrupar" - concept-neutral, leave it.)

`es/recordings.json`: line 11 `"Mover a carpeta…"`, line 14 `"Mover a carpeta"`.
`es/tour.json` line 13: `"Las grabaciones aparecen aquí a medida que terminan. Organízalas en carpetas, arrástralas para reordenarlas y suelta archivos de audio en cualquier parte de este panel para subirlos."`

- [ ] **Step 4: Rewrite the French values**

`apps/web/src/locales/fr/workspace.json` - *section* -> *dossier*, *sous-section* -> *sous-dossier*:
```
line 420  "newSectionPlaceholder": "Nom du nouveau dossier",
line 421  "newSection": "Nouveau dossier",
line 446  "ungrouped": "Sans dossier",
line 472  "confirmDeleteSection": "Supprimer le dossier « {{name}} » ? Ses enregistrements passeront dans Sans dossier.",
line 473  "sectionActions": "Actions du dossier",
line 474  "sectionNameAria": "Nom du dossier",
line 475  "newSectionNestCapped": "Les dossiers ne peuvent être imbriqués que sur 8 niveaux",
line 476  "newSubSection": "Nouveau sous-dossier",
line 477  "newSubSectionPlaceholder": "Nouveau sous-dossier dans {{parent}}",
line 531  "moveToSectionTitle": "Déplacer vers un dossier",
line 668  "searchFilterSection": "Dossier",
line 673  "roomCounts_one": "{{count}} dossier",
line 674  "roomCounts_other": "{{count}} dossiers",
```
`line 446` and `line 472` must move together, as in German.

`fr/recordings.json`: line 11 `"Déplacer vers un dossier…"`, line 14 `"Déplacer vers un dossier"`.
`fr/tour.json` line 13: `"Les enregistrements apparaissent ici une fois terminés. Organisez-les en dossiers, glissez-les pour les réordonner, et déposez des fichiers audio n'importe où sur ce panneau pour les importer."`

- [ ] **Step 5: Verify no folder-concept "section" survives in the catalogs**

```bash
cd apps/web/src/locales && grep -rn -iE "section|abschnitt|secci|sección" en de es fr
```
Expected: the only hits are **key names** (`newSectionPlaceholder`, `sectionActions`, `sectionSummary`, `sectionTranscript`, `detailSectionMeeting`, `sectionGroups`, `mtDragSection`, `moveToSection`, `searchFilterSection`, `confirmDeleteSection`, `newSectionNestCapped`, `roomCounts` has none), the `{{section}}` interpolation variable in `selectAllIn`, and the **template-section** values that are deliberately out of scope (`Add section`, `Section title`, `Section actions` under the `mt*` template keys, `Every section needs a title.`, and `account.json`'s "Per section (best structure)"). No *folder-concept* value may remain.

Read each hit before dismissing it. `workspace.json` has both a folder `sectionActions` (line 473, in scope) and a template `mtSectionActions` (line 608, out of scope) - do not confuse them.

- [ ] **Step 6: Run the web suite**

```bash
cd apps/web && npm test
```
Expected: PASS. Tests that assert on English UI strings ("Move to section", the Section chip) surface here; update the assertion to the new wording - the string changed, the behaviour did not.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/locales
git commit -m "fix(i18n): say Folder, not Section, everywhere the UI means a folder"
```

---

### Task 3: Help articles and API error strings

**Files:**
- Modify: `apps/web/src/content/help/en/organizing-folders.md:12,14,42`
- Modify: `apps/web/src/content/help/en/search-and-tags.md:18`
- Modify: `src/Diariz.Api/Controllers/SectionsController.cs:79,129,176`
- Test: `apps/web/src/content/help/helpContent.test.ts` (existing - run, don't edit)

**Interfaces:**
- Consumes: the English wording fixed in Task 2 (`Move to folder`, the `Folder` search chip) and the button from Task 1 - the help prose must describe what those now say and do.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Update `organizing-folders.md`**

Line 12 - the recording's move action, renamed in Task 2:
```
project underneath it. A recording can be filed into a folder at any of those levels from its
**Move to folder** action, not just the bottom one.
```

Line 14:
```
**Move to folder** opens a picker rather than a plain list: type a folder's name into the filter box
```

Line 42 - this bullet documents behaviour that Task 1 **changed**, so it is rewritten, not just reworded:
```
- When the path is too long to fit, the middle collapses behind an ellipsis, but the menu at the end
  still lists the whole hierarchy. Next to that menu, a page button opens the folder's own page.
```

- [ ] **Step 2: Update `search-and-tags.md`**

Line 18 - the chip label renamed in Task 2:
```
switches to *Everywhere*, results are grouped under the folder each meeting lives in, and **Folder**,
```

- [ ] **Step 3: Run the help-content test**

```bash
cd apps/web && npm test -- src/content/help/helpContent.test.ts
```
Expected: PASS. This enforces ASCII-only content and the `title`/`summary`/`group`/`order` front matter - neither of which these edits touch, but it is the guard that catches a smart quote or a stray dash slipping in.

- [ ] **Step 4: Update the API's raw-English error strings**

These three `BadRequest` bodies are not routed through the API's i18n catalogs - they reach the user verbatim as toasts. In `src/Diariz.Api/Controllers/SectionsController.cs`:

```csharp
line 79:   if (string.IsNullOrEmpty(name)) return BadRequest("Folder name is required.");
line 129:      if (ids.Contains(parentId)) return BadRequest("A folder cannot be its own parent.");
line 176:  if (string.IsNullOrEmpty(name)) return BadRequest("Folder name is required.");
```

The class, the route (`/api/sections`) and every identifier stay as they are - this is copy only.

- [ ] **Step 5: Verify no test pinned those strings, then build**

```bash
grep -rn "Section name is required\|cannot be its own parent" --include=*.cs .
```
Expected: only the three lines you just edited, now reading "Folder" - no test file hits.

```bash
dotnet build Diariz.slnx
```
Expected: build succeeds. (Build the whole solution, not just the API - a unit-test-only run misses integration-project compile breaks.)

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/content/help/en/organizing-folders.md apps/web/src/content/help/en/search-and-tags.md src/Diariz.Api/Controllers/SectionsController.cs
git commit -m "docs(help): describe the folder page button, and say Folder in API errors"
```

---

### Task 4: Version bump, release entry and feature docs

**Files:**
- Modify: `version.json`
- Modify: `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts` (new `RELEASES[0]` entry + 4 `CAPABILITIES` rows)
- Modify: `README.md` (Search feature row)
- Modify: `docs/features.md:281,332,374,410,429`
- Test: `apps/web/src/lib/releases.test.ts`, `apps/web/src/lib/versionMirrors.test.ts` (existing - run, don't edit)

**Interfaces:**
- Consumes: the behaviour delivered in Tasks 1-3 - the release entry and docs describe it.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Bump the version in all five places**

`version.json`:
```json
{ "version": "0.214.0" }
```
Then set the same `0.214.0` in: `apps/web/package.json` (`version`), `apps/desktop/package.json` (`version`), `src/Diariz.Api/Diariz.Api.csproj` (`<Version>`), `integrations/n8n-nodes-diariz/package.json` (`version`). All five must match or `versionMirrors.test.ts` fails - it exists because the n8n node silently sat at `0.1.0` for ~70 releases and a published npm version cannot be corrected afterwards.

- [ ] **Step 2: Update the four current CAPABILITIES rows**

In `apps/web/src/lib/releases.ts`, in the `CAPABILITIES` table (around lines 26-40), fix the folder-concept wording. The **Organise & merge** row describes behaviour Task 1 changed, so its clause is rewritten:

- Formulas row: `a whole folder and its sub-sections` -> `a whole folder and its sub-folders`
- Search row: `with Section / Date / Speaker chips` -> `with Folder / Date / Speaker chips`
- Rooms row: `its own folder structure (sections/sub-sections, drag-and-drop, per-room order)` -> `its own folder structure (folders/sub-folders, drag-and-drop, per-room order)`
- Organise & merge row: replace `with **Open section page** in the breadcrumb's menu as a separate target from browsing deeper` with `with a page button beside the breadcrumb's menu opening the folder's own page, a separate target from browsing deeper`

Leave every historical `RELEASES` entry alone - they record what shipped and what the UI said at the time.

- [ ] **Step 3: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`. Set `pr` to the **real** PR number - if the PR does not exist yet, put your best guess now and correct it in Task 5 Step 4 after `gh pr create` returns the actual number. Do not assume "last + 1": Dependabot PRs and issues share the sequence.

```ts
  {
    version: "0.214.0",
    date: "2026-08-15",
    pr: 528,
    headline: "A button for a folder's own page, and Folders called Folders",
    summary:
      "Opening a folder's own page used to mean opening the breadcrumb's dropdown and picking an entry off the top of a list otherwise made of parent folders. It is now a page button sitting right next to that dropdown, so it is one click and always visible; the dropdown goes back to being purely the list of parent folders. The app also calls them Folders consistently now - the New folder box, Move to folder, Folder actions, the Folder search chip and the room switcher's folder counts all said \"section\" in places, which is what the code calls them, not what you see.",
    changed: [
      "The list view's breadcrumb has a page button opening the folder's own page, next to the dropdown - previously an entry inside it.",
      "That dropdown is now purely the parent-folder chain.",
      "Folder wording is consistent across English, German, Spanish and French: New folder, Move to folder, Folder actions, Folder name, the Folder search chip, and \"12 folders\" in the room switcher.",
    ],
  },
```

`RELEASES[0].version` must equal `version.json` - `releases.test.ts` asserts it.

- [ ] **Step 4: Update the README and features.md in lockstep**

`README.md`, the **Search** feature row: `grouped by folder with Section / Date / Speaker chips` -> `grouped by folder with Folder / Date / Speaker chips`.

`docs/features.md` - the canonical prose list, which must move with the README:
- line 281: `**Section / Date / Speaker** chips` -> `**Folder / Date / Speaker** chips`
- line 332: `(section) page` -> `page` (the parenthetical existed only to bridge the two names)
- line 374: rewrite `ancestor chain plus **Open section page** - browsing deeper and opening the page stay separate targets` to `ancestor chain, with a page button beside the menu opening the folder's own page - browsing deeper and opening the page stay separate targets`
- line 410: `via the recording's Move to section action` -> `via the recording's Move to folder action`
- line 429: `Open any folder (section) as a **first-class page**` -> `Open any folder as a **first-class page**`

- [ ] **Step 5: Run the release and mirror tests**

```bash
cd apps/web && npm test -- src/lib/releases.test.ts src/lib/versionMirrors.test.ts
```
Expected: PASS. A failure here names exactly which mirror drifted or which version disagrees.

- [ ] **Step 6: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md
git commit -m "chore(release): 0.214.0 - folder page button and consistent Folder wording"
```

---

### Task 5: Full verification and PR

jsdom computes no geometry, so no test written so far proves the button actually looks or fits right. That needs the running app.

**Files:** none modified except a possible `pr:` correction in `apps/web/src/lib/releases.ts`.

**Interfaces:**
- Consumes: everything from Tasks 1-4.
- Produces: the pushed branch and the open PR.

- [ ] **Step 1: Run every suite**

```bash
cd apps/web && npm test && npm run build
```
Expected: all web tests PASS, clean `tsc`, successful vite build.

```bash
dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests
```
Expected: build succeeds, unit tests PASS. (The C# change is three string literals, but build the full solution - the integration project has a second construction site for several controllers and a unit-only run misses compile breaks there.)

- [ ] **Step 2: Verify in the running app**

Start the dev server via the preview tooling (never `npm run dev` through a shell tool), open the list view, and drill into a nested folder - the screenshot's case is `Customer Calls > ... > Headless Docgen CCMS Project`, so use a folder at least three deep so the path is collapsed.

Confirm, and screenshot:
1. The page button sits **immediately left** of the dropdown chevron.
2. Hovering it shows **"View folder page"**.
3. Clicking it opens the folder's page, and the list behind it stays drilled where it was (the URL keeps `?in=`).
4. The dropdown now lists **only** the parent folders - no "Open section page" entry, no divider above the chain.
5. At the room's root, neither the button nor the dropdown appears (the whole row is gone).
6. The 14px page glyph reads as a control next to the 12px chevron, and the row does not wrap or overflow when the left panel is dragged narrow. If it crowds, drop the icon to `size={13}` - and re-run `npm test` afterwards, since that is a code edit made after the last green run.
7. Toggle dark mode and confirm the hover states.

- [ ] **Step 3: Push the branch and open the PR**

```bash
git push -u origin feat/folder-page-button
```

Open the PR with `gh pr create`. The body must state:
- what changed (button promoted out of the dropdown; Folder wording sweep),
- **Deployment surface: server redeploy only - no desktop release** (nothing under `apps/desktop/src/**` or `build/**` changed; the version mirror bump alone does not require one),
- that `docs/Overall_Synopsis_of_Platform.md` and `docs/Data_Schema.md` are deliberately untouched - no component, contract, dependency, endpoint, schema or storage change,
- and the trailer:

```
🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

- [ ] **Step 4: Correct the PR number if it was a guess**

Compare the number `gh pr create` returned with the `pr:` field written in Task 4 Step 3. If they differ:

```bash
git add apps/web/src/lib/releases.ts
git commit -m "chore(release): correct the PR number in the 0.214.0 entry"
git push
```

No test catches a wrong PR number - it has to be checked by eye.

- [ ] **Step 5: Confirm CI is green**

```bash
gh pr checks --watch
```
Expected: all required checks pass. `main` is guarded by a ruleset with a strict up-to-date policy, so if the branch falls behind, rebase onto `main` and push again before merging.
