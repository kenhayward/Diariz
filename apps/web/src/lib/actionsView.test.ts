import { describe, expect, it } from "vitest";
import { completeTarget, distinctActors, filterActions } from "./actionsView";
import type { ActionListItem } from "./types";

const make = (over: Partial<ActionListItem>): ActionListItem => ({
  id: over.id ?? "1",
  recordingId: "r1",
  recordingName: "Rec",
  text: over.text ?? "do it",
  actor: over.actor ?? "",
  deadline: "",
  ordinal: 0,
  completed: over.completed ?? false,
  completedAt: over.completed ? "2026-06-30T00:00:00Z" : null,
  createdAt: "2026-06-30T00:00:00Z",
  ...over,
});

describe("distinctActors", () => {
  it("returns sorted unique non-empty actor names", () => {
    const actions = [make({ actor: "Bob" }), make({ actor: "Alice" }), make({ actor: "Bob" }), make({ actor: "  " }), make({ actor: "" })];
    expect(distinctActors(actions)).toEqual(["Alice", "Bob"]);
  });

  it("trims surrounding whitespace before de-duping", () => {
    expect(distinctActors([make({ actor: "Bob" }), make({ actor: " Bob " })])).toEqual(["Bob"]);
  });
});

describe("filterActions", () => {
  const actions = [
    make({ id: "a", actor: "Bob", completed: false }),
    make({ id: "b", actor: "Alice", completed: true }),
    make({ id: "c", actor: "Bob", completed: true }),
  ];

  it("keeps everything with no filters", () => {
    expect(filterActions(actions, { person: null, hideComplete: false }).map((a) => a.id)).toEqual(["a", "b", "c"]);
  });

  it("filters by exact person", () => {
    expect(filterActions(actions, { person: "Bob", hideComplete: false }).map((a) => a.id)).toEqual(["a", "c"]);
  });

  it("hides completed actions", () => {
    expect(filterActions(actions, { person: null, hideComplete: true }).map((a) => a.id)).toEqual(["a"]);
  });

  it("combines person + hide-complete", () => {
    expect(filterActions(actions, { person: "Bob", hideComplete: true }).map((a) => a.id)).toEqual(["a"]);
  });
});

describe("completeTarget", () => {
  it("completes when none/some selected are complete", () => {
    expect(completeTarget([{ completed: false }, { completed: true }])).toBe(true);
    expect(completeTarget([{ completed: false }])).toBe(true);
  });

  it("un-completes only when every selected is already complete", () => {
    expect(completeTarget([{ completed: true }, { completed: true }])).toBe(false);
  });

  it("defaults to complete for an empty selection", () => {
    expect(completeTarget([])).toBe(true);
  });
});
