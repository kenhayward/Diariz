import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

const { api } = vi.hoisted(() => ({
  api: { discoverModels: vi.fn(), importModels: vi.fn() },
}));
vi.mock("../../lib/api", () => ({ api, apiErrorMessage: (e: unknown) => String(e) }));

import DiscoverModelsDialog from "./DiscoverModelsDialog";

const FOUND = [
  { id: "gpt-4o", contextLength: 128000, contextLengthReported: true, alreadyExists: false },
  { id: "llama-3.3-70b", contextLength: 16384, contextLengthReported: false, alreadyExists: false },
  { id: "already-here", contextLength: 8192, contextLengthReported: true, alreadyExists: true },
];

function open(onImported = vi.fn()) {
  render(<DiscoverModelsDialog onClose={vi.fn()} onImported={onImported} />);
  fireEvent.change(screen.getByLabelText(/endpoint/i), { target: { value: "http://lm.test/v1" } });
  fireEvent.change(screen.getByLabelText(/key/i), { target: { value: "sk-secret" } });
  fireEvent.click(screen.getByRole("button", { name: /discover/i }));
  return onImported;
}

describe("DiscoverModelsDialog", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.discoverModels as Mock).mockResolvedValue(FOUND);
    (api.importModels as Mock).mockResolvedValue({ added: 2, skipped: 0, needContextLength: [] });
  });

  it("queries the endpoint the administrator typed", async () => {
    open();

    await waitFor(() =>
      expect(api.discoverModels).toHaveBeenCalledWith({
        apiBase: "http://lm.test/v1",
        apiKey: "sk-secret",
      }),
    );
  });

  it("will not discover without an endpoint", () => {
    render(<DiscoverModelsDialog onClose={vi.fn()} onImported={vi.fn()} />);

    fireEvent.click(screen.getByRole("button", { name: /discover/i }));

    expect(api.discoverModels).not.toHaveBeenCalled();
  });

  it("pre-ticks new models and locks off ones already defined", async () => {
    open();
    await screen.findByRole("checkbox", { name: /gpt-4o/ });

    expect((screen.getByRole("checkbox", { name: /gpt-4o/ }) as HTMLInputElement).checked).toBe(true);
    const existing = screen.getByRole("checkbox", { name: /already-here/ }) as HTMLInputElement;
    expect(existing.checked).toBe(false);
    expect(existing.disabled).toBe(true);
  });

  it("flags a context length the endpoint did not report", async () => {
    // 16384 is a guess. An administrator who cannot tell it from a measured value has no reason to correct
    // it, and it silently sizes both the chat dial and the real context budget.
    open();
    const row = (await screen.findByText(/llama-3\.3-70b/)).closest("li")!;

    expect(row.textContent).toMatch(/not reported/i);
    expect((await screen.findByText(/gpt-4o/)).closest("li")!.textContent).not.toMatch(/not reported/i);
  });

  it("counts the ticked models in the confirm button", async () => {
    open();
    expect(await screen.findByRole("button", { name: /add 2 models/i })).toBeTruthy();
  });

  it("imports exactly what is ticked", async () => {
    open();
    await screen.findByRole("checkbox", { name: /llama-3\.3-70b/ });

    fireEvent.click(screen.getByRole("checkbox", { name: /llama-3\.3-70b/ }));
    fireEvent.click(screen.getByRole("button", { name: /add 1 model/i }));

    await waitFor(() => expect(api.importModels).toHaveBeenCalled());
    expect((api.importModels as Mock).mock.calls[0][0].names).toEqual(["gpt-4o"]);
    expect((api.importModels as Mock).mock.calls[0][0].apiBase).toBe("http://lm.test/v1");
  });

  it("never offers to import a model that already exists", async () => {
    open();
    await screen.findByRole("checkbox", { name: /gpt-4o/ });

    fireEvent.click(screen.getByRole("button", { name: /add 2 models/i }));

    await waitFor(() => expect(api.importModels).toHaveBeenCalled());
    expect((api.importModels as Mock).mock.calls[0][0].names).not.toContain("already-here");
  });

  it("tells the caller once models have been imported", async () => {
    const onImported = open();
    await screen.findByRole("checkbox", { name: /gpt-4o/ });

    fireEvent.click(screen.getByRole("button", { name: /add 2 models/i }));

    await waitFor(() => expect(onImported).toHaveBeenCalled());
  });

  it("shows an empty state rather than an enabled zero-model import", async () => {
    (api.discoverModels as Mock).mockResolvedValue([]);
    open();

    expect(await screen.findByText(/no models/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /add .* model/i })).toBeNull();
  });

  it("reports a failure instead of an empty listing", async () => {
    // An unreachable endpoint and a reachable one with nothing on it are different problems, and the
    // administrator needs to know which they have.
    (api.discoverModels as Mock).mockRejectedValue(new Error("Connection refused"));
    open();

    expect(await screen.findByText(/connection refused/i)).toBeTruthy();
  });
});
