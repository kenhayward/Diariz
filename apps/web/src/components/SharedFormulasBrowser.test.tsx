import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { fromPrompt } from "../lib/formulaTemplate";

vi.mock("../lib/api", () => ({
  api: {
    listSharedFormulas: vi.fn(),
    subscribeFormula: vi.fn(),
    unsubscribeFormula: vi.fn(),
  },
  apiErrorMessage: (e: unknown, fallback: string) => {
    const resp = (e as { response?: { data?: string } })?.response?.data;
    return resp ?? fallback ?? String(e);
  },
}));

import { api } from "../lib/api";
import SharedFormulasBrowser from "./SharedFormulasBrowser";
import type { SharedFormula } from "../lib/types";

const sharedFormula = (over: Partial<SharedFormula> = {}): SharedFormula => ({
  formula: {
    id: "f1",
    scope: "Personal",
    ownerUserId: "u2",
    name: "Team Recap",
    description: "Recap the meeting",
    content: fromPrompt("Summarize the transcript."),
    context: 1,
    enabled: true,
    isBuiltIn: false,
    shared: true,
  },
  ownerName: "Alice Owner",
  ownerPictureUrl: null,
  alreadyAdded: false,
  ...over,
});

function renderBrowser() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={qc}>
      <SharedFormulasBrowser onClose={onClose} />
    </QueryClientProvider>,
  );
  return { onClose };
}

describe("SharedFormulasBrowser", () => {
  beforeEach(() => vi.clearAllMocks());

  it("lists a shared formula with its owner name and Add button", async () => {
    (api.listSharedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([sharedFormula()]);
    renderBrowser();
    await screen.findByText("Team Recap");
    expect(screen.getByText(/shared by alice owner/i)).toBeTruthy();
    expect(screen.getByRole("button", { name: /^add$/i })).toBeTruthy();
  });

  it("clicking Add calls subscribeFormula", async () => {
    (api.listSharedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([sharedFormula({ formula: { ...sharedFormula().formula, id: "fx" } })]);
    (api.subscribeFormula as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderBrowser();
    await screen.findByText("Team Recap");
    fireEvent.click(screen.getByRole("button", { name: /^add$/i }));
    await waitFor(() => expect(api.subscribeFormula).toHaveBeenCalledWith("fx"));
  });

  it("an already-added row shows Remove and calls unsubscribeFormula", async () => {
    (api.listSharedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      sharedFormula({ formula: { ...sharedFormula().formula, id: "fy" }, alreadyAdded: true }),
    ]);
    (api.unsubscribeFormula as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderBrowser();
    await screen.findByText("Team Recap");
    const remove = screen.getByRole("button", { name: /^remove$/i });
    expect(remove).toBeTruthy();
    fireEvent.click(remove);
    await waitFor(() => expect(api.unsubscribeFormula).toHaveBeenCalledWith("fy"));
  });

  it("renders the empty state when no one has shared a formula", async () => {
    (api.listSharedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    renderBrowser();
    expect(await screen.findByText(/no one has shared a formula yet/i)).toBeTruthy();
  });

  it("toggles the read-only prompt with View/Hide", async () => {
    (api.listSharedFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([sharedFormula()]);
    renderBrowser();
    await screen.findByText("Team Recap");
    expect(screen.queryByText("Summarize the transcript.")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /^view$/i }));
    expect(screen.getByText("Summarize the transcript.")).toBeTruthy();
  });
});
