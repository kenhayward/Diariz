import { describe, it, expect } from "vitest";
import {
  emptyToolCallLine,
  toolStarted,
  toolEnded,
  clearToolCallLine,
  toolCallLineText,
} from "./toolCallLine";

describe("toolCallLine", () => {
  it("shows nothing when idle", () => {
    expect(toolCallLineText(emptyToolCallLine)).toBeNull();
  });

  it("shows a gray line for one in-flight call", () => {
    const s = toolStarted(emptyToolCallLine, "who_said_that");
    expect(toolCallLineText(s)).toBe("Tool call: who_said_that…");
  });

  it("lists multiple concurrent calls on the same line", () => {
    let s = toolStarted(emptyToolCallLine, "who_said_that");
    s = toolStarted(s, "list_recordings");
    expect(toolCallLineText(s)).toBe("Tool call: who_said_that… list_recordings…");
  });

  it("removes a call when it ends, leaving no trace once all finish", () => {
    let s = toolStarted(emptyToolCallLine, "who_said_that");
    s = toolStarted(s, "list_recordings");
    s = toolEnded(s, "who_said_that");
    expect(toolCallLineText(s)).toBe("Tool call: list_recordings…");
    s = toolEnded(s, "list_recordings");
    expect(toolCallLineText(s)).toBeNull();
  });

  it("clears entirely", () => {
    let s = toolStarted(emptyToolCallLine, "who_said_that");
    s = clearToolCallLine();
    expect(toolCallLineText(s)).toBeNull();
  });
});
