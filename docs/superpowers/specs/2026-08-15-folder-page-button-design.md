# Folder-page button + "Folders everywhere" wording sweep

**Date:** 2026-08-15
**Status:** approved, ready to plan
**Deployment surface:** server redeploy only (web + API strings). No desktop release.
**Version:** 0.213.0 -> 0.214.0 (functional enhancement: Minor +1, Build reset)

## Problem

Opening a folder's own page is a first-class navigation target, but it is reachable only by opening the
breadcrumb's chevron menu and picking "Open section page >" from the top of a list otherwise made of
ancestor crumbs. It is buried, and it is the odd one out in that menu: every other entry drills the list,
while this one navigates the middle panel.

Separately, the app deliberately calls these things **Folders** in the UI while code, DB and API say
**Section** - but the UI has drifted. A user still sees "New section", "Move to section", "Section actions",
a **Section** search chip, and "12 sections" in the room switcher.

## Goals

1. Promote "open this folder's page" to a permanent icon button, immediately left of the breadcrumb's
   dropdown chevron, with hover text **"View folder page"**.
2. Remove the now-duplicated menu entry, leaving the dropdown as purely the ancestor chain.
3. Eliminate user-visible "section" wording wherever it means *folder*, across all four locales, help
   articles, the About box, README/features docs, and the API's raw-English error strings.

## Non-goals

- Renaming anything in code, routes, query keys, DTOs, C# types or SQL. `SectionsController`,
  `/api/sections`, `sectionId`, `SectionDetail.tsx` etc. all stay. This is a UX-copy change only.
- Touching the *other* meaning of "section": minutes/formula **template sections** ("Add section",
  "Section title", "Every section needs a title.", "Per section (best structure)"), page-area references
  ("Calendar Event section", "Notes-tab section"), and the in-app API reference's endpoint groups. Those
  use the word correctly.
- Rewriting historical `RELEASES` entries in `apps/web/src/lib/releases.ts`. Past entries are a record of
  what shipped and what the UI said at the time. Only current copy (`CAPABILITIES`) is corrected.

## Design

### The button

`FolderPath` (`apps/web/src/components/nav/FolderPath.tsx`) owns the chevron trigger, and lays out:

```
<div relative flex>  <nav flex-1>...crumbs...</nav>  <button>chevron</button>  {menu}  </div>
```

"Immediately left of the dropdown" therefore has to be *inside* `FolderPath`. It gains one optional prop:

```ts
/// Rendered between the path and the menu trigger.
trailingAction?: ReactNode;
```

rendered between `</nav>` and the chevron `<button>`. `FolderPath` stays presentational - it renders a
node and knows nothing about what the node does, preserving the boundary its class comment already sets
out ("it takes crumbs, callbacks and ... knows nothing about drilling or rooms").

`DrillBreadcrumb` (`apps/web/src/components/nav/DrillBreadcrumb.tsx`) passes a react-router `<Link>`:

- **Element:** `<Link>`, not `<button>` - so ctrl-click, middle-click and "open in new tab" keep working,
  exactly as the menu entry already did (it used the `to` branch of `extraItems` for this reason).
- **Destination:** `` `${basePath}/sections/${current.id}` `` + `useDrillSearch()`'s `?in=<id>`. Identical to
  today's menu item, so room-prefixing (`useRoomBasePath()` via the `basePath` prop) and drill-state
  preservation carry over unchanged.
- **Icon:** `FileTextIcon` at `size={14}` - Feather `file-text`, a page glyph, currently used only by the
  Preferences Formulas tab.
- **Accessible name + tooltip:** both `title` and `aria-label` set to `t("drillOpenFolderPage")` =
  **"View folder page"**. (Both, per the `ToolbarButton` convention. The button is never disabled, so the
  `disabled:pointer-events-none` tooltip-swallowing gotcha noted in `ListToolbar.tsx` does not apply and no
  wrapper span is needed.)
- **Styling:** matches the chevron trigger -
  `shrink-0 rounded p-0.5 text-gray-400 hover:bg-gray-100 hover:text-gray-700 dark:text-gray-500 dark:hover:bg-gray-800 dark:hover:text-gray-200`.

### Where it does and does not appear

Both conditions already exist and are reused rather than reinvented:

- **Root:** `DrillBreadcrumb.tsx:33` returns `null` when `sectionId === null`, so the entire row - back
  button, folder glyph, path, chevron - is absent at the room's top level. The button inherits this. No new
  guard.
- **Unresolvable folder:** the button is gated on `current` being truthy, exactly as `extraItems` is today.
  If the folder was deleted while the user was inside it, `breadcrumbOf` returns an empty chain and only the
  back button renders - the way out, not a link to a dead page.
- **A folder's own page** (`SectionDetail.tsx`) renders `FolderPath` too, and passes no `trailingAction` -
  you are already on that page.

### Removing the menu entry

`DrillBreadcrumb` is the **only** consumer of `FolderPath`'s `extraItems` prop. With the entry promoted,
the prop is dead, so it is removed entirely along with its `Link`/`button` render branch and the `border-t`
divider that separated it from the ancestor chain. The dropdown becomes purely the ancestor list.

The class comments in both files document *why* a crumb click and opening the page are distinct targets
("collapsing them would make it impossible to reach a folder's page once you had drilled in"). That
reasoning still holds - it is now a button rather than a menu entry - so the comments are rewritten, not
deleted.

### Wording sweep

All folder-concept strings. Four locales; de/es/fr use *Ordner* / *carpeta* / *dossier*.

**`apps/web/src/locales/{en,de,es,fr}/workspace.json`**

| Key | en now | en after |
|---|---|---|
| `newSectionPlaceholder` | New section name | New folder name |
| `newSection` | New section | New folder |
| `drillOpenSectionPage` | Open section page > | *(key renamed)* `drillOpenFolderPage` = **View folder page** (no trailing `>`; it is a tooltip, not a menu entry) |
| `confirmDeleteSection` | Delete section "{{name}}"? ... | Delete folder "{{name}}"? ... |
| `sectionActions` | Section actions | Folder actions |
| `sectionNameAria` | Section name | Folder name |
| `newSubSection` | New sub-section | New sub-folder |
| `newSubSectionPlaceholder` | New sub-section in {{parent}} | New sub-folder in {{parent}} |
| `moveToSectionTitle` | Move to section | Move to folder |
| `searchFilterSection` | Section | Folder |
| `roomCounts_one` / `_other` | {{count}} section(s) | {{count}} folder(s) |

Locale-specific extras:
- `ungrouped` - **de** "Ohne Abschnitt" -> "Ohne Ordner", **fr** "Sans section" -> "Sans dossier". (en
  "Ungrouped" and es "Sin agrupar" are already concept-neutral.) `confirmDeleteSection` in de and fr quotes
  this label, so both must move together.
- `newSectionNestCapped` - en already reads "Folders can only be nested 8 levels deep"; de, es and fr were
  never updated and still say Abschnitte / secciones / sections.

Key names stay as-is apart from `drillOpenSectionPage` -> `drillOpenFolderPage` (renamed because its value
and role both change, and it must be renamed in all four files together).

**`apps/web/src/locales/{en,de,es,fr}/recordings.json`** - `moveToSection` ("Move to section...") and
`moveToSectionShort` ("Move to section").

**`apps/web/src/locales/{en,de,es,fr}/tour.json`** - `recordings.body`: "Organise them into sections" ->
"into folders".

**`apps/web/src/content/help/en/**`** (English only - there is no localised help):
- `organizing-folders.md` - two **Move to section** references, and the bullet stating the menu "still lists
  the whole hierarchy, including **Open section page** for that folder's own page". That last one documents
  behaviour that is changing, so it must describe the button instead.
- `search-and-tags.md` - the **Section** chip.

**`apps/web/src/lib/releases.ts`** - the four current `CAPABILITIES` rows: Formulas ("sub-sections"),
Search ("Section / Date / Speaker chips"), Rooms ("sections/sub-sections"), and Organise & merge, which
names **Open section page** "in the breadcrumb's menu" and so must be rewritten to describe the button.

**`src/Diariz.Api/Controllers/SectionsController.cs`** - three raw-English `BadRequest` bodies that surface
to the user as toasts (they are not routed through the API's i18n catalogs): "Section name is required."
(lines 79 and 176) and "A section cannot be its own parent." (line 129). No test pins these strings.

**`README.md` / `docs/features.md`** - the Search chip row in both; plus `features.md`'s Organise & merge
paragraph (which documents the menu placement), its Move to section reference, and the "(section)"
parentheticals.

## Testing

TDD - each test written and seen to fail before the code that satisfies it.

- **`DrillBreadcrumb.test.tsx`** - new: a link named "View folder page" with href
  `/sections/<id>?in=<id>`, and the `/rooms/r1`-prefixed variant. The three existing tests that open the
  menu and click `menuitem /open section page/i` invert: the menu now contains only the ancestor chain and
  no such item. The existing "renders nothing at root" test already covers the button's absence at root;
  extend its assertion to name the button explicitly so the root rule is pinned to this feature, not
  inherited by accident.
- **`FolderPath.test.tsx`** - the two `extraItems` tests are deleted with the prop. New: `trailingAction`
  renders, and renders **before** the menu trigger in DOM order (structure, not geometry - valid in jsdom;
  asserted via node order within the wrapper, not by any geometric measure).
- **`RecordingsPanel.test.tsx`** - the integration test at ~line 345 that opens the folder page from the
  breadcrumb menu switches to clicking the button.
- Plain assertions only - `@testing-library/jest-dom` is not a dependency of this project and must not
  become one.
- **Browser pass** on the running app: confirm the 14px page glyph reads as a button beside the 12px
  chevron, that the row does not wrap or overflow at narrow panel widths, and that it is absent at root.
  jsdom cannot answer any of those.

## Release checklist

1. `version.json` 0.213.0 -> **0.214.0**, plus all four mirrors: `apps/web/package.json`,
   `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`,
   `integrations/n8n-nodes-diariz/package.json`.
2. One new `RELEASES[0]` entry in `apps/web/src/lib/releases.ts` (version, date, real PR number, headline,
   prose summary, changed/fixed bullets).
3. `CAPABILITIES` rows as above. No new third-party library or model, so `AboutModal.tsx` disclaimers are
   untouched.
4. README Features rows.
5. `docs/features.md` rows, in lockstep with the README.
6. `docs/Overall_Synopsis_of_Platform.md` - **no change**. No component, queue, contract, dependency,
   endpoint or deployment detail changes.
7. `docs/Data_Schema.md` - **no change**. No schema or storage change.

Also: no em/en dashes in any user-facing string added or edited; help articles stay ASCII-only.
