import { test } from "node:test";
import assert from "node:assert/strict";
import { accumulateSse } from "../nodes/Diariz/transport/sse";

async function* chunks(...parts: string[]) {
  for (const p of parts) yield Buffer.from(p, "utf8");
}

const frame = (o: unknown) => `data: ${JSON.stringify(o)}\n\n`;

test("concatenates token values across frames", async () => {
  const r = await accumulateSse(
    chunks(frame({ type: "token", value: "Hello" }), frame({ type: "token", value: " world" })),
  );
  assert.equal(r.answer, "Hello world");
});

test("handles a frame split across chunk boundaries", async () => {
  const r = await accumulateSse(chunks('data: {"type":"tok', 'en","value":"split"}\n\n'));
  assert.equal(r.answer, "split");
});

test("collects citations without putting them in the answer", async () => {
  const r = await accumulateSse(
    chunks(
      frame({ type: "token", value: "See this." }),
      frame({ type: "ref", name: "Weekly sync", href: "https://d/x" }),
    ),
  );
  assert.equal(r.answer, "See this.");
  assert.deepEqual(r.references, [{ name: "Weekly sync", href: "https://d/x" }]);
});

test("stops at the done frame and reports the model", async () => {
  const r = await accumulateSse(
    chunks(frame({ type: "token", value: "a" }), frame({ type: "done", model: "gpt-oss" })),
  );
  assert.equal(r.answer, "a");
  assert.equal(r.model, "gpt-oss");
});

test("throws with the server's message on an error frame", async () => {
  await assert.rejects(
    accumulateSse(chunks(frame({ type: "error", message: "No AI endpoint is configured." }))),
    /No AI endpoint is configured/,
  );
});

test("ignores meta and tool frames", async () => {
  const r = await accumulateSse(
    chunks(
      frame({ type: "meta", model: "m", contextUsed: 1 }),
      frame({ type: "tool_start", name: "search" }),
      frame({ type: "token", value: "ok" }),
      frame({ type: "tool_end", name: "search" }),
    ),
  );
  assert.equal(r.answer, "ok");
});

test("survives a malformed frame rather than throwing", async () => {
  const r = await accumulateSse(chunks("data: not-json\n\n", frame({ type: "token", value: "ok" })));
  assert.equal(r.answer, "ok");
});

test("returns an empty answer for an empty stream", async () => {
  const r = await accumulateSse(chunks());
  assert.equal(r.answer, "");
  assert.deepEqual(r.references, []);
});
