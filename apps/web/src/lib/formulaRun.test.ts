import { describe, it, expect, vi } from "vitest";
import { awaitFormulaResult } from "./formulaRun";

type Row = { id: string; status: string; error: string | null };

/// A poll that returns each scripted snapshot in turn, repeating the last one forever.
const poller = (snapshots: Row[][]) => {
  let i = 0;
  return vi.fn(async () => snapshots[Math.min(i++, snapshots.length - 1)]);
};

const generating: Row = { id: "r1", status: "Generating", error: null };
const ready: Row = { id: "r1", status: "Ready", error: null };
const failed: Row = { id: "r1", status: "Failed", error: "The LLM request timed out." };

// A sleep that resolves immediately, so the tests don't actually wait.
const sleep = () => Promise.resolve();
const opts = { intervalMs: 100, timeoutMs: 1000, sleep };

describe("awaitFormulaResult", () => {
  it("returns ready as soon as the run finishes", async () => {
    const poll = poller([[generating], [generating], [ready]]);
    expect(await awaitFormulaResult("r1", poll, opts)).toEqual({ kind: "ready" });
    expect(poll).toHaveBeenCalledTimes(3);
  });

  it("returns ready on the first poll when the worker was already done", async () => {
    const poll = poller([[ready]]);
    expect(await awaitFormulaResult("r1", poll, opts)).toEqual({ kind: "ready" });
    expect(poll).toHaveBeenCalledTimes(1);
  });

  it("surfaces the failure reason rather than pretending the run worked", async () => {
    const poll = poller([[generating], [failed]]);
    expect(await awaitFormulaResult("r1", poll, opts)).toEqual({
      kind: "failed",
      error: "The LLM request timed out.",
    });
  });

  it("gives up once the timeout is reached instead of polling forever", async () => {
    const poll = poller([[generating]]);
    expect(await awaitFormulaResult("r1", poll, { ...opts, intervalMs: 100, timeoutMs: 300 })).toEqual({
      kind: "timeout",
    });
    // Polls at 0, 100, 200, 300ms - then stops.
    expect(poll).toHaveBeenCalledTimes(4);
  });

  it("reports the result vanishing (deleted while the run was in flight)", async () => {
    const poll = poller([[generating], []]);
    expect(await awaitFormulaResult("r1", poll, opts)).toEqual({ kind: "gone" });
  });

  it("ignores other results on the same recording", async () => {
    const other: Row = { id: "other", status: "Ready", error: null };
    const poll = poller([[other, generating], [other, ready]]);
    expect(await awaitFormulaResult("r1", poll, opts)).toEqual({ kind: "ready" });
  });

  it("still checks once when the timeout is zero", async () => {
    const poll = poller([[ready]]);
    expect(await awaitFormulaResult("r1", poll, { ...opts, timeoutMs: 0 })).toEqual({ kind: "ready" });
  });
});
