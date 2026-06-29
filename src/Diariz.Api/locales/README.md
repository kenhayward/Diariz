# Server-side export strings

The labels used in **downloaded transcripts** (`.txt` / `.md` / `.rtf`) and the **emailed transcript** are
localized from the JSON files in this folder — `exports.json` per language — read at runtime by
`JsonExportLocalizer` (no compiled `.resx`). The language is the recording owner's **app language**
(`UserSettings.UiLanguage`); English is authoritative and is the fallback for any missing key.

```
locales/
  en/exports.json   # authoritative
  es/  fr/  de/ …    # one folder per language, same keys
```

These mirror the web UI catalogs' approach: **adding or improving a language is a data-only PR** — copy
`en/exports.json` to a new `xx/` folder, translate the values, keep the keys. The files are copied next to
the published app (see the `Content` item in `Diariz.Api.csproj`), so they can also be edited or mounted in
a deployment without a rebuild.

Keys: `transcriptName`, `summary`, `actions`, `transcript`, `action`, `actor`, `deadline`, `time`,
`speaker`, `text`, `none`, `sentFromDiariz`, `subject` (a template containing `{name}`).
