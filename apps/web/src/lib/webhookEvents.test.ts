import { describe, it, expect } from "vitest";
import type { TFunction } from "i18next";
import { WEBHOOK_EVENT_KEYS, webhookEvents, webhookEventGroups } from "./webhookEvents";

/// The real `t` is bound to the account namespace; here the key itself is the label, which keeps these
/// tests about grouping rather than about copy.
const t = ((key: string) => key) as unknown as TFunction;

describe("webhookEventGroups", () => {
  it("covers every subscribable event exactly once", () => {
    const grouped = webhookEventGroups(t).flatMap((g) => g.events.map((e) => e.key));
    expect(grouped.sort()).toEqual([...WEBHOOK_EVENT_KEYS].sort());
    expect(new Set(grouped).size).toBe(WEBHOOK_EVENT_KEYS.length);
  });

  it("sorts a recording's own lifecycle apart from what is generated from it", () => {
    const groups = webhookEventGroups(t);
    expect(groups.map((g) => g.id)).toEqual(["recordings", "documents", "formulas"]);
    expect(groups[0].events.map((e) => e.key)).toEqual([
      "recording.created",
      "recording.transcribed",
      "recording.transcription_failed",
    ]);
    expect(groups[2].events.map((e) => e.key)).toEqual(["formula_result.completed", "formula_result.failed"]);
  });

  it("keeps each event's existing label rather than inventing a second set", () => {
    const flat = new Map(webhookEvents(t).map((e) => [e.key, e.label]));
    for (const group of webhookEventGroups(t)) {
      for (const evt of group.events) expect(evt.label).toBe(flat.get(evt.key));
    }
  });

  // The composer groups the picker; the server still takes a flat list, and the order it goes out in is
  // the order the user sees.
  it("preserves the canonical event order when flattened", () => {
    const grouped = webhookEventGroups(t).flatMap((g) => g.events.map((e) => e.key));
    expect(grouped).toEqual([...WEBHOOK_EVENT_KEYS]);
  });
});
