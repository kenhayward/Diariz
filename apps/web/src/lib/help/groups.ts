import { FALLBACK_GROUP } from "./parseArticle";

/// The nav-tree sections, in display order. An article's `group:` front-matter field names one of these
/// ids; `label` is an i18n key in the `help` namespace, so the section headings translate even though
/// the article prose is English-only.
///
/// Adding a section is a one-line change here plus the matching key in every `locales/*/help.json`
/// (`locales.test.ts` enforces that key parity).
export const HELP_GROUPS: { id: string; label: string }[] = [
  { id: "getting-started", label: "groupGettingStarted" },
  { id: "recordings", label: "groupRecordings" },
  { id: "asking-questions", label: "groupAskingQuestions" },
  { id: "settings", label: "groupSettings" },
  { id: FALLBACK_GROUP, label: "groupOther" },
];

export const HELP_GROUP_IDS = HELP_GROUPS.map((g) => g.id);
