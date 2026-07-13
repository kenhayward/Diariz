import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi } from "vitest";
import type { ReactElement } from "react";
import FormulasPanel from "./FormulasPanel";
import type { FormulaResult } from "../lib/types";

vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k, i18n: { language: "en" } }),
}));
vi.mock("../lib/api", () => ({
  api: { getFormulaResultText: vi.fn().mockResolvedValue("# Hello\n\nBody text.") },
  apiErrorMessage: (_e: unknown, f: string) => f,
}));

const base = {
  recordingId: "r1",
  createdByUserId: "u1",
  createdAt: "2026-07-13T00:00:00Z",
  updatedAt: "2026-07-13T00:00:00Z",
  origin: { kind: "personal" as const, personName: "Ada", personPictureUrl: null },
};
const results: FormulaResult[] = [{ ...base, id: "a", name: "Recap" }];

function wrap(ui: ReactElement) {
  const qc = new QueryClient();
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

describe("FormulasPanel", () => {
  it("renders the empty state (no split) when there are no runs", () => {
    wrap(<FormulasPanel recordingId="r1" results={[]} selectedId={null} onSelect={() => {}} />);
    expect(screen.getByText("formulasEmpty")).toBeTruthy();
    expect(screen.queryByRole("separator")).toBeNull();
  });

  it("prompts to select when a split is shown but nothing is selected", () => {
    wrap(<FormulasPanel recordingId="r1" results={results} selectedId={null} onSelect={() => {}} />);
    expect(screen.getByText("formulaSelectToView")).toBeTruthy();
    expect(screen.getByRole("separator")).toBeTruthy();
  });

  it("renders the selected result's markdown in the right panel", async () => {
    wrap(<FormulasPanel recordingId="r1" results={results} selectedId="a" onSelect={() => {}} />);
    await waitFor(() => expect(screen.getByText("Hello")).toBeTruthy());
    expect(screen.getByText("Body text.")).toBeTruthy();
  });
});
