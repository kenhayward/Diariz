import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ReactElement } from "react";
import FormulasPanel from "./FormulasPanel";
import type { FormulaResult } from "../lib/types";

vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k, i18n: { language: "en" } }),
}));
vi.mock("../lib/api", () => ({
  apiErrorMessage: (_e: unknown, f: string) => f,
}));

const loadText = vi.fn<(id: string) => Promise<string>>();
beforeEach(() => {
  loadText.mockReset();
  loadText.mockResolvedValue("# Hello\n\nBody text.");
});

const base = {
  recordingId: "r1",
  status: "Ready" as const,
  error: null,
  createdByUserId: "u1",
  createdAt: "2026-07-13T00:00:00Z",
  updatedAt: "2026-07-13T00:00:00Z",
  origin: { kind: "personal" as const, personName: "Ada", personPictureUrl: null },
};
const results: FormulaResult[] = [{ ...base, id: "a", name: "Recap" }];
const sourceKey = ["formula-result-text", "r1"];

function wrap(ui: ReactElement) {
  const qc = new QueryClient();
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

describe("FormulasPanel", () => {
  it("renders the empty state (no split) when there are no runs", () => {
    wrap(<FormulasPanel loadText={loadText} sourceKey={sourceKey} results={[]} selectedId={null} onSelect={() => {}} />);
    expect(screen.getByText("formulasEmpty")).toBeTruthy();
    expect(screen.queryByRole("separator")).toBeNull();
  });

  it("prompts to select when a split is shown but nothing is selected", () => {
    wrap(
      <FormulasPanel loadText={loadText} sourceKey={sourceKey} results={results} selectedId={null} onSelect={() => {}} />,
    );
    expect(screen.getByText("formulaSelectToView")).toBeTruthy();
    expect(screen.getByRole("separator")).toBeTruthy();
  });

  it("renders the selected result's markdown via the injected loadText", async () => {
    wrap(
      <FormulasPanel loadText={loadText} sourceKey={sourceKey} results={results} selectedId="a" onSelect={() => {}} />,
    );
    await waitFor(() => expect(screen.getByText("Hello")).toBeTruthy());
    expect(screen.getByText("Body text.")).toBeTruthy();
    expect(loadText).toHaveBeenCalledWith("a");
  });

  it("shows the generating state and does not fetch text for a Generating selection", () => {
    const gen: FormulaResult[] = [{ ...base, id: "g", name: "Recap", status: "Generating" }];
    wrap(<FormulasPanel loadText={loadText} sourceKey={sourceKey} results={gen} selectedId="g" onSelect={() => {}} />);
    // Shown in both the left row's meta and the right panel.
    expect(screen.getAllByText("formulaGenerating").length).toBeGreaterThan(0);
    expect(loadText).not.toHaveBeenCalled();
  });

  it("shows the failed state (with the error) and does not fetch text for a Failed selection", () => {
    const failed: FormulaResult[] = [{ ...base, id: "f", name: "Recap", status: "Failed", error: "Model timed out" }];
    wrap(<FormulasPanel loadText={loadText} sourceKey={sourceKey} results={failed} selectedId="f" onSelect={() => {}} />);
    // Shown in both the left row and the right panel.
    expect(screen.getAllByText(/model timed out/i).length).toBeGreaterThan(0);
    expect(loadText).not.toHaveBeenCalled();
  });
});
