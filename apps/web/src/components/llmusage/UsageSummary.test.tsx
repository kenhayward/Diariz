import { render, screen, fireEvent, within } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import type { LlmUsageSummary, LlmUsageSummaryGroup, LlmUsageTotals } from "../../lib/types";
import UsageSummary from "./UsageSummary";

function group(overrides: Partial<LlmUsageSummaryGroup> = {}): LlmUsageSummaryGroup {
  return {
    userId: "u1",
    userEmail: "alice@example.com",
    model: "gpt-4o",
    kind: "Summarize",
    calls: 4,
    operations: 2,
    averageTurnsPerOperation: 2,
    maxTurnsPerOperation: 3,
    promptTokens: 400,
    completionTokens: 200,
    reasoningTokens: null,
    totalTokens: 600,
    tokenMeasuredCalls: 3,
    failedCalls: 0,
    tokensPerSecond: 12.5,
    ...overrides,
  };
}

function totals(overrides: Partial<LlmUsageTotals> = {}): LlmUsageTotals {
  return {
    calls: 10,
    operations: 6,
    durationMs: 12000,
    promptTokens: 900,
    completionTokens: 500,
    reasoningTokens: null,
    totalTokens: 1400,
    tokenMeasuredCalls: 9,
    promptTokensMeasured: 9,
    completionTokensMeasured: 8,
    reasoningTokensMeasured: 0,
    totalTokensMeasured: 7,
    failedCalls: 1,
    tokensPerSecond: 15,
    ...overrides,
  };
}

function summary(groups: LlmUsageSummaryGroup[], totalsObj: LlmUsageTotals): LlmUsageSummary {
  return { groups, totals: totalsObj };
}

describe("UsageSummary", () => {
  it("renders one column per requested group dimension, and only those", async () => {
    render(
      <UsageSummary
        summary={summary([group()], totals())}
        isLoading={false}
        isError={false}
        groupBy={["kind", "model"]}
        onGroupByChange={vi.fn()}
      />,
    );

    expect(await screen.findByRole("columnheader", { name: "Type" })).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: "Model" })).toBeTruthy();
    // "user" was not requested - no User column, even though every group carries a userEmail.
    expect(screen.queryByRole("columnheader", { name: "User" })).toBeNull();
  });

  it("renders the average and maximum turns per operation exactly as reported, never a sum", async () => {
    // averageTurnsPerOperation * operations would be 2 * 2 = 4, and maxTurnsPerOperation * operations
    // would be 3 * 2 = 6 - neither of those numbers may appear as if it were the rendered figure.
    render(
      <UsageSummary
        summary={summary([group({ averageTurnsPerOperation: 2, maxTurnsPerOperation: 3, operations: 2 })], totals())}
        isLoading={false}
        isError={false}
        groupBy={["kind"]}
        onGroupByChange={vi.fn()}
      />,
    );

    const row = await screen.findByTestId("llm-usage-summary-row-0");
    expect(within(row).getByText("2.0")).toBeTruthy();
    expect(within(row).getByText("3")).toBeTruthy();
  });

  it("never renders a null token field as 0 on a group row, and captions it with the group's own measured count", async () => {
    render(
      <UsageSummary
        summary={summary([group({ reasoningTokens: null, tokenMeasuredCalls: 3, calls: 4 })], totals())}
        isLoading={false}
        isError={false}
        groupBy={["kind"]}
        onGroupByChange={vi.fn()}
      />,
    );

    const row = await screen.findByTestId("llm-usage-summary-row-0");
    expect(within(row).getByText("-")).toBeTruthy();
    expect(row.textContent).toContain("measured on 3 of 4 calls");
  });

  it("renders the totals row from summary.totals using each column's own measured count, not a fold over the groups", async () => {
    // The two groups' promptTokens sum to 400+400=800 - the totals object below reports something else
    // (900), so a test where the two happened to agree would prove nothing.
    const groups = [group({ promptTokens: 400 }), group({ promptTokens: 400, kind: "Tags" })];
    const apiTotals = totals({ promptTokens: 900, promptTokensMeasured: 9, calls: 10 });
    render(
      <UsageSummary
        summary={summary(groups, apiTotals)}
        isLoading={false}
        isError={false}
        groupBy={["kind"]}
        onGroupByChange={vi.fn()}
      />,
    );

    const totalsRow = await screen.findByTestId("llm-usage-summary-totals-row");
    expect(within(totalsRow).getByText("900")).toBeTruthy();
    expect(within(totalsRow).queryByText("800")).toBeNull();
    expect(totalsRow.textContent).toContain("measured on 9 of 10 calls");
  });

  it("sorts groups client-side on a header click, without requesting anything", async () => {
    const groups = [
      group({ kind: "Summarize", calls: 3 }),
      group({ kind: "Tags", calls: 9 }),
      group({ kind: "Dictation", calls: 5 }),
    ];
    render(
      <UsageSummary
        summary={summary(groups, totals())}
        isLoading={false}
        isError={false}
        groupBy={["kind"]}
        onGroupByChange={vi.fn()}
      />,
    );

    // A freshly-picked column starts ascending, same convention UsageTable's own sort headers use.
    fireEvent.click(await screen.findByTestId("llm-usage-summary-sort-calls"));

    const rows = screen.getAllByTestId(/llm-usage-summary-row-/);
    expect(within(rows[0]).getByText("Summary")).toBeTruthy(); // calls=3, lowest first ascending
    expect(within(rows[1]).getByText("Dictation")).toBeTruthy(); // calls=5
    expect(within(rows[2]).getByText("Tags")).toBeTruthy(); // calls=9

    // Clicking the SAME column again toggles direction to descending.
    fireEvent.click(screen.getByTestId("llm-usage-summary-sort-calls"));
    const rowsDesc = screen.getAllByTestId(/llm-usage-summary-row-/);
    expect(within(rowsDesc[0]).getByText("Tags")).toBeTruthy(); // calls=9, highest first descending
  });

  it("toggles a group-by chip on and off, but never lets the selection reach zero dimensions", async () => {
    const onGroupByChange = vi.fn();
    render(
      <UsageSummary
        summary={summary([group()], totals())}
        isLoading={false}
        isError={false}
        groupBy={["kind"]}
        onGroupByChange={onGroupByChange}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    expect(onGroupByChange).toHaveBeenCalledWith(["model", "kind"]);

    onGroupByChange.mockClear();
    // "Type" is the only selected dimension in this render's props - clicking it off would leave zero
    // dimensions selected, which the server 400s on, so the component must refuse the click.
    fireEvent.click(screen.getByRole("button", { name: "Type" }));
    expect(onGroupByChange).not.toHaveBeenCalled();
  });

  it("shows an empty-state message when no groups match the filters", async () => {
    render(
      <UsageSummary
        summary={summary([], totals({ calls: 0, operations: 0 }))}
        isLoading={false}
        isError={false}
        groupBy={["kind"]}
        onGroupByChange={vi.fn()}
      />,
    );
    expect(await screen.findByText(/no usage matches these filters/i)).toBeTruthy();
  });

  it("shows a load error instead of a stale or empty table", () => {
    render(
      <UsageSummary summary={undefined} isLoading={false} isError={true} groupBy={["kind"]} onGroupByChange={vi.fn()} />,
    );
    expect(screen.getByText(/usage summary couldn't be loaded/i)).toBeTruthy();
  });
});
