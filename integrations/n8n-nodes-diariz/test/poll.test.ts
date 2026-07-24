import { test } from "node:test";
import assert from "node:assert/strict";
import { pollUntilTerminal } from "../nodes/Diariz/transport/poll";

const noSleep = async () => {};

const classify = (v: { status: string }) =>
  v.status === "Ready" ? "ready" : v.status === "Failed" ? "failed" : "pending";

test("returns as soon as the result is ready", async () => {
  const states = ["Generating", "Generating", "Ready"];
  let i = 0;
  const result = await pollUntilTerminal(async () => ({ status: states[i++] }), classify, {
    intervalMs: 1,
    timeoutMs: 1000,
    sleep: noSleep,
  });
  assert.equal(result.status, "Ready");
  assert.equal(i, 3);
});

test("does not sleep at all when the first poll is already ready", async () => {
  let slept = 0;
  const result = await pollUntilTerminal(async () => ({ status: "Ready" }), classify, {
    intervalMs: 1,
    timeoutMs: 1000,
    sleep: async () => {
      slept++;
    },
  });
  assert.equal(result.status, "Ready");
  assert.equal(slept, 0);
});

test("throws with the recorded error when the run fails", async () => {
  await assert.rejects(
    pollUntilTerminal(
      async () => ({ status: "Failed", error: "The LLM request timed out." }),
      classify,
      { intervalMs: 1, timeoutMs: 1000, sleep: noSleep },
    ),
    /The LLM request timed out/,
  );
});

test("gives up after the timeout", async () => {
  let clock = 0;
  await assert.rejects(
    pollUntilTerminal(async () => ({ status: "Generating" }), classify, {
      intervalMs: 1,
      timeoutMs: 10,
      sleep: noSleep,
      now: () => (clock += 4),
    }),
    /still generating|timed out/i,
  );
});

test("surfaces the fetch error rather than swallowing it", async () => {
  await assert.rejects(
    pollUntilTerminal(
      async () => {
        throw new Error("network down");
      },
      classify,
      { intervalMs: 1, timeoutMs: 100, sleep: noSleep },
    ),
    /network down/,
  );
});
