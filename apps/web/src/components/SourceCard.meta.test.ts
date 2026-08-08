import { describe, it, expect } from "vitest";

/// A `SourceCard`'s meta line is one truncating line in a header that also carries a status chip and the
/// source's own controls. Three of these have now shipped too long and been cut mid-word - "Claude
/// Desktop, C...", "· 1 automation...", "needs a model that supports..." - each spotted only once it was
/// in front of someone.
///
/// So they are capped. The number is a proxy for a width, not a truth: it is set just above the longest
/// string that has been seen to render whole, and its job is to fail in CI rather than in a screenshot.
/// A translation that genuinely needs more room is a reason to shorten the English, not to raise the cap.
///
/// It cannot see how crowded a given header is, which is its real limit: the Outlook card carries a chip,
/// a checkbox and a button, and truncated a 47-character line straight through this gate. The fix there
/// was to unclutter the header rather than to lower the cap for every card - so a card that fails to fit
/// while passing here is telling you its header is doing too much.
const MAX_META = 90;

/// Every key rendered as a card's single-line `meta`, plus the one sub-heading that sits under one.
const META_KEYS = [
  "integrationsMcpMeta",
  "integrationsApiMeta",
  "integrationsAutomationsMeta",
  "integrationsConnectedAppsHint",
  "assistantToolsMeta",
  "calendarsGoogleMeta",
  "calendarsOutlookMeta",
  "calendarsFeedsMeta",
];

const modules = import.meta.glob("../locales/*/account.json", { eager: true }) as Record<
  string,
  { default: Record<string, string> }
>;

describe("card meta lines fit on one line", () => {
  for (const path in modules) {
    const lng = /\/locales\/([^/]+)\//.exec(path)![1];
    const data = modules[path].default;

    it(`${lng} keeps every card meta under ${MAX_META} characters`, () => {
      const tooLong = META_KEYS.filter((k) => (data[k] ?? "").length > MAX_META).map(
        (k) => `${k} (${data[k].length})`,
      );
      expect(tooLong, `these would be truncated in the card header: ${tooLong.join(", ")}`).toEqual([]);
    });
  }
});
