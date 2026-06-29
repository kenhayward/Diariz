# UI translations

The Diariz web interface is localized with [react-i18next](https://react.i18next.com/). Every piece of
on-screen text comes from a JSON catalog in this folder — **you can add a language or improve an existing
one by editing JSON only, no code changes**.

## Layout

```
locales/
  languages.json          # metadata for every language (code, names, RTL flag)
  en/                      # English — the authoritative base; every other language mirrors its keys
    common.json
    auth.json
    account.json
    recordings.json
  es/  fr/  de/  …         # one folder per translated language, same files & keys as en/
```

Catalogs are **auto-discovered** (`import.meta.glob`), so dropping in a new `xx/` folder registers the
language with no code change. The app's language picker shows exactly the languages that have a catalog
folder here (intersected with `languages.json`).

## Add or improve a language

1. **Copy `en/` to a new folder** named with the language's BCP-47 code (e.g. `pt-BR/`), or edit an
   existing folder to improve it.
2. **Translate the values, keep the keys.** Every file must have the **same keys as `en/`** — don't add,
   remove, or rename keys. Leave `{{placeholders}}` (e.g. `{{language}}`, `{{value}}`) intact.
3. **Add a row to `languages.json`** if the language isn't already listed: `{ "code", "englishName",
   "nativeName", "rtl" }`. Set `"rtl": true` for right-to-left scripts (Arabic, Hebrew, Persian, Urdu).
4. **Open a PR.** Keep one language per PR so it can be reviewed in isolation (the CI gate enforces this;
   PRs that also change code or `en` are exempt).

## What CI checks

- **Completeness & validity** (`src/locales.test.ts`, runs in `npm test`): every catalog is valid JSON
  with a key set **exactly equal to `en`'s**, with no empty values, and every locale folder has a
  `languages.json` entry. Missing keys still fall back to English at runtime, but a complete catalog is
  required to merge.
- **One language per translation PR** (`scripts/check-single-locale.mjs`): a translation-only PR may touch
  at most one non-English locale folder.

## Notes for developers adding new strings

When you add UI text, add the key to the relevant **`en/`** namespace (and ideally the flagship locales).
Use `useTranslation("<namespace>")` and `t("key")`, or the fully-qualified `t("namespace:key")`. The
namespaces today are `common`, `auth`, `account`, and `recordings`.
