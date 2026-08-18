import { render, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
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
    <MemoryRouter>
      <TestResultCard
        result={result}
        group="Summaries"
        resolvedTimeoutSeconds={120}
        apiBase="http://llm.test/v1"
        modelName="m"
        onFix={onFix}
        onRetry={vi.fn()}
        {...props}
      />
    </MemoryRouter>,
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

describe("TestResultCard actions", () => {
  const writeText = vi.fn();

  beforeEach(() => {
    writeText.mockReset().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
  });

  it("copies a cURL command for the request that actually ran", async () => {
    // Rebuilding it from the editor's state afterwards would quote a request that was never sent - the
    // whole point is to reproduce THIS call outside Diariz.
    show(OK);

    fireEvent.click(screen.getByRole("button", { name: /curl/i }));

    await vi.waitFor(() => expect(writeText).toHaveBeenCalled());
    const command = writeText.mock.calls[0][0] as string;
    expect(command).toContain("http://llm.test/v1/chat/completions");
    expect(command).toContain('{"model":"m"}');
  });

  it("never puts a credential in the copied command", async () => {
    // The browser is never given the key - it is write-only - so the command names a placeholder to
    // substitute. A command that silently omitted authentication would fail for a reason the admin would
    // then have to diagnose separately.
    show(OK);

    fireEvent.click(screen.getByRole("button", { name: /curl/i }));

    await vi.waitFor(() => expect(writeText).toHaveBeenCalled());
    const command = writeText.mock.calls[0][0] as string;
    expect(command).toMatch(/\$LLM_API_KEY/);
    expect(command).not.toMatch(/sk-|Bearer [a-z0-9]{8}/i);
  });

  it("copies the whole result as JSON", async () => {
    show(OK);

    fireEvent.click(screen.getByRole("button", { name: /raw json/i }));

    await vi.waitFor(() => expect(writeText).toHaveBeenCalled());
    expect(JSON.parse(writeText.mock.calls[0][0] as string).durationMs).toBe(1420);
  });

  it("offers a retry only when there is something to retry", () => {
    show(OK);
    expect(screen.queryByRole("button", { name: /retry/i })).toBeNull();
  });

  it("retries a failure", () => {
    const onRetry = vi.fn();
    show(TIMEOUT, { onRetry });

    fireEvent.click(screen.getByRole("button", { name: /retry/i }));

    expect(onRetry).toHaveBeenCalled();
  });

  it("hands the usage-log filter to its host when there is no page to navigate to", () => {
    // Inside the settings modal there is no route to go to - navigating would drop the admin out of the
    // modal (and, in the desktop shell, out of the app). The host opens its own usage panel instead.
    const onOpenUsageLog = vi.fn();
    show(OK, { onOpenUsageLog });

    fireEvent.click(screen.getByRole("button", { name: /usage log/i }));

    expect(onOpenUsageLog).toHaveBeenCalledWith(
      expect.stringContaining("kinds=AdminTest"),
    );
    expect(screen.queryByRole("link", { name: /usage log/i })).toBeNull();
  });

  it("links into the usage log filtered to this model's test calls", () => {
    // Without the filter the link lands on every call the platform has made this week, which is not what
    // "open in usage log" promises.
    show(OK);

    const href = screen.getByRole("link", { name: /usage log/i }).getAttribute("href") ?? "";
    expect(href).toContain("/admin/llm-usage?");
    expect(href).toContain("kinds=AdminTest");
    expect(decodeURIComponent(href)).toContain("models=m");
  });
});
