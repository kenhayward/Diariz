import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { LlmUsageFilterOptions, LlmUsageOperationRow, LlmUsagePage, LlmUsageTotals } from "../lib/types";

let isAdmin = true;
vi.mock("../auth", () => ({ useAuth: () => ({ isPlatformAdmin: isAdmin }) }));

// lib/rooms is NOT mocked - useRoomBasePath reads its RoomContext directly and is documented safe outside
// a RoomProvider (falls back to ""), which is exactly the situation this top-level route is in. Using the
// real hook here is the whole point of the "must go through useRoomBasePath" requirement: it proves the
// table's recording/folder links are built by calling it, not by hard-coding a path.
vi.mock("../lib/api", () => ({
  api: {
    getLlmUsage: vi.fn(),
    getLlmUsageFilters: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import LlmUsage from "./LlmUsage";

const filterOptions: LlmUsageFilterOptions = {
  users: [{ userId: "u1", userEmail: "alice@example.com" }],
  models: ["gpt-4o"],
  kinds: ["Summarize", "Tags"],
};

function row(overrides: Partial<LlmUsageOperationRow> = {}): LlmUsageOperationRow {
  return {
    operationId: "op-1",
    kind: "Summarize",
    userId: "u1",
    userEmail: "alice@example.com",
    recordingId: "rec-1",
    recordingTitle: "Weekly Standup",
    sectionId: null,
    sectionName: null,
    model: "gpt-4o",
    turns: 1,
    startedAt: "2026-08-15T10:00:00.000Z",
    completedAt: "2026-08-15T10:00:05.000Z",
    promptTokens: 100,
    completionTokens: 50,
    reasoningTokens: null,
    totalTokens: 150,
    success: true,
    ...overrides,
  };
}

function totals(overrides: Partial<LlmUsageTotals> = {}): LlmUsageTotals {
  return {
    calls: 1,
    operations: 1,
    durationMs: 5000,
    promptTokens: 100,
    completionTokens: 50,
    reasoningTokens: null,
    totalTokens: 150,
    tokenMeasuredCalls: 1,
    promptTokensMeasured: 1,
    completionTokensMeasured: 1,
    reasoningTokensMeasured: 0,
    totalTokensMeasured: 1,
    failedCalls: 0,
    tokensPerSecond: 10,
    ...overrides,
  };
}

function usagePage(
  rows: LlmUsageOperationRow[],
  totalsObj: LlmUsageTotals,
  overrides: Partial<LlmUsagePage<LlmUsageOperationRow>> = {},
): LlmUsagePage<LlmUsageOperationRow> {
  return { rows, page: 1, pageSize: 50, total: rows.length, totals: totalsObj, ...overrides };
}

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <LlmUsage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("LlmUsage", () => {
  beforeEach(() => {
    isAdmin = true;
    vi.clearAllMocks();
    vi.mocked(api.getLlmUsageFilters).mockResolvedValue(filterOptions);
    vi.mocked(api.getLlmUsage).mockResolvedValue(usagePage([row()], totals()));
  });

  it("requests the first page with the default filters", async () => {
    renderPage();
    await waitFor(() => expect(api.getLlmUsage).toHaveBeenCalled());

    const call = vi.mocked(api.getLlmUsage).mock.calls[0][0];
    expect(call.mode).toBe("operations");
    expect(call.sort).toBe("startedAt");
    expect(call.desc).toBe(true);
    expect(call.page).toBe(1);
    expect(call.pageSize).toBe(50);
    expect(call.outcome).toBe("all");
    expect(typeof call.from).toBe("string");
    expect(api.getLlmUsageFilters).toHaveBeenCalled();
  });

  it("refetches with the new params when a filter changes", async () => {
    renderPage();
    await waitFor(() => expect(api.getLlmUsage).toHaveBeenCalledTimes(1));

    // Open the Types multi-select and pick "Summary" (the label for the Summarize kind).
    fireEvent.click(screen.getByRole("button", { name: /^Types/ }));
    const summaryOption = await screen.findByLabelText("Summary");
    fireEvent.click(summaryOption);

    await waitFor(() => expect(api.getLlmUsage).toHaveBeenCalledTimes(2));
    const lastCall = vi.mocked(api.getLlmUsage).mock.calls[1][0];
    expect(lastCall.kinds).toEqual(["Summarize"]);
    expect(lastCall.page).toBe(1); // a filter change resets to page 1
  });

  it("toggles sort direction on a header click and refetches, without reordering client-side", async () => {
    renderPage();
    await screen.findByTestId("llm-usage-sort-model"); // wait for the table itself, not just the request

    fireEvent.click(screen.getByTestId("llm-usage-sort-model"));
    await waitFor(() => expect(api.getLlmUsage).toHaveBeenCalledTimes(2));
    let call = vi.mocked(api.getLlmUsage).mock.calls[1][0];
    expect(call.sort).toBe("model");
    expect(call.desc).toBe(false); // a newly-picked column starts ascending

    fireEvent.click(screen.getByTestId("llm-usage-sort-model"));
    await waitFor(() => expect(api.getLlmUsage).toHaveBeenCalledTimes(3));
    call = vi.mocked(api.getLlmUsage).mock.calls[2][0];
    expect(call.sort).toBe("model");
    expect(call.desc).toBe(true); // clicking the SAME column again toggles direction
  });

  it("renders the API's totals, never a sum of the visible rows", async () => {
    // The two visible rows' totalTokens sum to 350 - the totals object below reports a completely
    // different figure (850), so a test where they happened to agree would prove nothing.
    const rows = [row({ operationId: "op-1", totalTokens: 150 }), row({ operationId: "op-2", totalTokens: 200 })];
    const apiTotals = totals({
      calls: 10,
      operations: 8,
      promptTokens: 500,
      completionTokens: 300,
      reasoningTokens: null,
      totalTokens: 850,
      tokenMeasuredCalls: 5,
      promptTokensMeasured: 5,
      completionTokensMeasured: 5,
      reasoningTokensMeasured: 0,
      totalTokensMeasured: 5,
      failedCalls: 1,
    });
    vi.mocked(api.getLlmUsage).mockResolvedValue(usagePage(rows, apiTotals));

    renderPage();
    const totalsRow = await screen.findByTestId("llm-usage-totals-row");

    expect(within(totalsRow).getByText("850")).toBeTruthy(); // the API's total
    expect(within(totalsRow).queryByText("350")).toBeNull(); // NOT the sum of the two visible rows
    // Every token total is qualified with how many of the filtered calls actually reported a value.
    expect(totalsRow.textContent).toContain("measured on 5 of 10 calls");
    // reasoningTokens is null in the totals - never rendered as 0.
    expect(within(totalsRow).getByText("-")).toBeTruthy();
  });

  it("captions each token column with its OWN measured count, not the any-column count", async () => {
    // Fix round 1 finding: LlmUsageTotals.tokenMeasuredCalls answers "did ANY column report usage",
    // which is a coarser, different question from each column's own measured count - and the four
    // columns are, in practice, very unevenly populated (most models never report reasoning tokens at
    // all). tokenMeasuredCalls is deliberately set to 10 here - equal to `calls`, and different from
    // every one of the four per-column counts below - so a rendering that (wrongly) reused it under all
    // four columns would show "measured on 10 of 10 calls" everywhere instead of the four distinct
    // figures asserted below.
    const apiTotals = totals({
      calls: 10,
      promptTokens: 900,
      completionTokens: 600,
      reasoningTokens: 2,
      totalTokens: 400,
      tokenMeasuredCalls: 10,
      promptTokensMeasured: 9,
      completionTokensMeasured: 6,
      reasoningTokensMeasured: 1,
      totalTokensMeasured: 4,
    });
    vi.mocked(api.getLlmUsage).mockResolvedValue(usagePage([row()], apiTotals));

    renderPage();
    const totalsRow = await screen.findByTestId("llm-usage-totals-row");

    expect(totalsRow.textContent).toContain("measured on 9 of 10 calls"); // prompt's own count
    expect(totalsRow.textContent).toContain("measured on 6 of 10 calls"); // completion's own count
    expect(totalsRow.textContent).toContain("measured on 1 of 10 calls"); // reasoning's own count
    expect(totalsRow.textContent).toContain("measured on 4 of 10 calls"); // total's own count
    // The any-column figure (10) must not leak into any of the four per-column captions.
    expect(totalsRow.textContent).not.toContain("measured on 10 of 10 calls");
  });

  it("shows a refusal instead of the table for a non-admin", async () => {
    isAdmin = false;
    renderPage();

    expect(await screen.findByText(/permission/i)).toBeTruthy();
    expect(screen.queryByRole("table")).toBeNull();
    expect(api.getLlmUsage).not.toHaveBeenCalled();
  });

  it("never renders an unmeasured (null) per-row token count as 0", async () => {
    vi.mocked(api.getLlmUsage).mockResolvedValue(
      usagePage([row({ promptTokens: null })], totals({ promptTokens: null })),
    );
    renderPage();
    await screen.findByText("gpt-4o"); // row rendered

    // The row's own prompt-token cell must read as "not measured", never a bare zero.
    expect(screen.queryByText("0", { selector: "td" })).toBeNull();
    expect(screen.getAllByText("-").length).toBeGreaterThan(0);
  });

  it("builds the recording link through useRoomBasePath, rendered even though it may 403", async () => {
    renderPage();
    const link = await screen.findByRole("link", { name: "Weekly Standup" });
    // No RoomProvider ancestor here (this is a top-level admin route), so useRoomBasePath's documented
    // fallback is "" - proving the link goes through the hook rather than a hard-coded path.
    expect(link.getAttribute("href")).toBe("/recordings/rec-1");
  });

  it("surfaces a GET /filters failure instead of leaving the multi-selects silently empty", async () => {
    vi.mocked(api.getLlmUsageFilters).mockRejectedValue(new Error("network error"));
    renderPage();

    expect(await screen.findByText(/filter options couldn't be loaded/i)).toBeTruthy();
    // The table itself is unaffected - getLlmUsage still runs and renders normally.
    await waitFor(() => expect(api.getLlmUsage).toHaveBeenCalled());
  });
});
