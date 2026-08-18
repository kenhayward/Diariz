import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import type { LlmTestOutcome } from "../../lib/types";
import TestResultCard from "./TestResultCard";

const OK: LlmTestOutcome = {
  ok: true, httpStatus: 200, ttftMs: 310, durationMs: 1420,
  promptTokens: 1240, completionTokens: 44, reasoningTokens: 128, totalTokens: 1412,
  finishReason: "stop", response: "The team reviewed Q3 spend.",
  requestBodyJson: '{"model":"m"}', errorKind: null, message: null, offendingParameter: null,
};

const TIMEOUT: LlmTestOutcome = {
  ...OK, ok: false, httpStatus: null, ttftMs: null, durationMs: 120000,
  promptTokens: null, completionTokens: null, reasoningTokens: null, totalTokens: null,
  finishReason: null, response: null, errorKind: "Timeout", message: "No response within 120s.",
};

const REJECTED: LlmTestOutcome = {
  ...TIMEOUT, durationMs: 80, httpStatus: 400, errorKind: "Http400",
  message: '"top_k" is not supported by this endpoint', offendingParameter: "top_k",
};

function show(result: LlmTestOutcome, props: Record<string, unknown> = {}) {
  const onFix = vi.fn();
  render(
    <TestResultCard
      result={result}
      group="Summaries"
      resolvedTimeoutSeconds={120}
      onFix={onFix}
      {...props}
    />,
  );
  return onFix;
}

describe("TestResultCard", () => {
  it("reports tokens per second from the completion tokens, not the total", () => {
    // 44 completion tokens in 1.42s is 31.0/s. Dividing the 1,412 TOTAL would read 994/s - a number that
    // looks like a fast model and is off by a factor of thirty. The usage log's column means the former.
    show(OK);

    expect(screen.getByText("31.0")).toBeTruthy();
    expect(screen.queryByText(/994/)).toBeNull();
  });

  it("leads with the time to the first token, separately from the duration", () => {
    show(OK);

    expect(screen.getByText("0.31 s")).toBeTruthy();
    expect(screen.getByText("1.42 s")).toBeTruthy();
  });

  it("shows what the model actually replied", () => {
    show(OK);

    expect(screen.getByText(/The team reviewed Q3 spend/)).toBeTruthy();
  });

  it("says a count was not measured rather than showing it as zero", () => {
    // A server that reports no usage has not told us the reply was free.
    show({ ...OK, completionTokens: null, totalTokens: null });

    expect(screen.queryByText("0.0")).toBeNull();
    expect(screen.getAllByText("-").length).toBeGreaterThan(0);
  });

  it("calls a timeout a timeout, and offers the fix on this call type", () => {
    const onFix = show(TIMEOUT);

    expect(screen.getByText(/gave up after/i)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /timeout/i }));

    // A longer timeout, set here rather than everywhere: the group is what the admin is looking at.
    expect(onFix).toHaveBeenCalledWith({ key: "timeout_seconds", value: 600 });
  });

  it("offers to omit the parameter an endpoint rejected", () => {
    // null, not undefined: the fix is "stop sending this", not "let a lower layer decide" - which would
    // put the same rejected value straight back on the wire.
    const onFix = show(REJECTED);

    fireEvent.click(screen.getByRole("button", { name: /omit/i }));

    expect(onFix).toHaveBeenCalledWith({ key: "top_k", value: null });
  });

  it("offers no parameter fix when the endpoint blamed nothing", () => {
    show({ ...REJECTED, offendingParameter: null, message: "Model not found" });

    expect(screen.queryByRole("button", { name: /omit/i })).toBeNull();
    expect(screen.getByText(/Model not found/)).toBeTruthy();
  });

  it("shows the endpoint's own words rather than a generic failure", () => {
    // The message names the thing to change. Replacing it with "the test failed" throws that away.
    show(REJECTED);

    expect(screen.getByText(/is not supported by this endpoint/)).toBeTruthy();
  });

  it("flags a reply that was cut off by a token cap", () => {
    // Same signal the usage log carries: a 200 with a short answer is otherwise indistinguishable from a
    // model that had little to say.
    show({ ...OK, finishReason: "length" });

    expect(screen.getByText(/cut off/i)).toBeTruthy();
  });
});
