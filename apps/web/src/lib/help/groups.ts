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
  // Admin and power-user territory: configuring formulas, RBAC, and the integration surfaces. Last,
  // because most readers never need it.
  { id: "advanced", label: "groupAdvanced" },
  // A worked pattern rather than a feature: Diariz ships no CRM connector, so these articles teach the
  // n8n/Zapier recipes that stand in for one. Separated from "advanced" because the reader is chasing an
  // outcome ("get my meetings into the CRM") rather than configuring a Diariz surface.
  { id: "crm", label: "groupCrm" },
  { id: FALLBACK_GROUP, label: "groupOther" },
];

export const HELP_GROUP_IDS = HELP_GROUPS.map((g) => g.id);
