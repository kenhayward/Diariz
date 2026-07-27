# Help content

User-facing documentation, rendered at `/help` and by the contextual `?` buttons. One Markdown file per
article; **the filename is the slug**, so `en/formulas.md` is served at `/help/formulas`.

These are **user** docs - task-oriented "how do I / what happens if" prose. They are deliberately not
kept in lockstep with the README Features table, `docs/features.md`, or the About-box `CAPABILITIES`,
which are *inventories* of what exists. Update an article when the behaviour a user relies on changes.

## Adding an article

Drop a file in `en/`. There is no registry to update - `lib/help/content.ts` discovers it with
`import.meta.glob`, the same way the i18n catalogs are discovered.

```markdown
---
title: Recording a meeting
summary: Capture from your microphone, system audio, or both. Diariz uploads the audio when you stop and transcribes it in the background.
group: getting-started
order: 20
---

## Starting a recording

Press the record button...
```

| Field | Required | Notes |
|---|---|---|
| `title` | yes | Shown in the nav, as the page heading, and in the `?` popover. |
| `summary` | yes | **This is what the `?` popover shows.** Two or three sentences, 240 characters max. |
| `group` | yes | One of the ids in `lib/help/groups.ts`. |
| `order` | no | Sorts within the group. Defaults to 999, so unordered articles sort last. |

The front matter is **not YAML** - only `key: value` scalar lines. That is deliberate: no parser
dependency, and content cannot grow structure the loader does not understand.

Start the body at `##`. The article's `title` is already rendered as the page's `<h1>`.

## Screenshots

Put images in an `images/` folder **next to the article**, and reference them relatively:

```
en/
  formulas.md
  images/
    formula-editor.png
```

```markdown
![The formula editor, with the Context checkboxes highlighted](./images/formula-editor.png)
```

Both `./images/x.png` and `images/x.png` work. The path in the source is not the path in the build -
Vite fingerprints assets - so `lib/help/images.ts` rewrites it to the emitted URL before rendering.

- **Absolute paths** (`/logo.png`, i.e. a file in `public/`) and **external URLs** are left alone.
- **Always write real alt text.** It is what a screen-reader user gets instead of the picture.
- Images are capped at the column width and given a border, so a full-resolution capture is fine.
- A file under about 4 KB is inlined into the bundle as a data URI; larger ones are emitted as separate
  files. Either way it just works.
- A path that resolves to nothing **fails the build** (see below) rather than shipping a broken image.

## Translations

Prose is English-only for now. The loader already resolves the active locale and falls back to `en`, so
adding `de/formulas.md` is all a translation takes - no code change. An untranslated article still
appears, in English.

A localised article uses its own `de/images/x.png` if one exists and otherwise falls back to the English
screenshot, which is better than a broken image.

Only *chrome* (nav labels, the search box, buttons) lives in the i18n catalogs, at
`locales/<lang>/help.json`. Long-form prose in JSON would be unreviewable, and `locales.test.ts` key
parity would force a four-file edit for every wording change.

## The rules, and what enforces them

`helpContent.test.ts` runs in `npm test` and fails the build on any of these:

- **ASCII only.** No smart quotes, em dashes, or accented characters. The failure names the file, the
  line, and the offending character. This is also how CLAUDE.md's no-em-dash rule is enforced.
- Every article parses and declares a non-empty `title`, a `summary` of 240 characters or fewer, and a
  `group` that exists.
- Every screenshot an article references actually exists.
- Every `<HelpButton topic="..." />` in the app resolves to a real article, so a `?` can never open onto
  nothing.

## Adding a contextual `?`

```tsx
import HelpButton from "./HelpButton";

<h3 className="flex items-center gap-1.5 ...">
  {t("tabFormulas")}
  <HelpButton topic="formulas" />
</h3>
```

`topic` is an article slug. The popover shows that article's `title` and `summary` with a link through
to the full page, so there is no second copy of the text to keep in step.
