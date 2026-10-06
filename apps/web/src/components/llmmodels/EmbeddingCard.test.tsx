import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { EmbeddingSettings } from "../../lib/types";

const { api } = vi.hoisted(() => ({
  api: {
    getEmbeddingSettings: vi.fn(),
    saveEmbeddingSettings: vi.fn(),
    testEmbedding: vi.fn(),
  },
}));
vi.mock("../../lib/api", () => ({ api, apiErrorMessage: (e: unknown) => String(e) }));

import EmbeddingCard from "./EmbeddingCard";

function settings(overrides: Partial<EmbeddingSettings> = {}): EmbeddingSettings {
  return {
    source: "Server",
    effectiveApiBase: "http://env.test/v1",
    model: "nomic-embed-text",
    dimension: 768,
    savedApiBase: null,
    savedHasApiKey: false,
    serverApiBase: "http://env.test/v1",
    ...overrides,
  };
}

function renderCard() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <EmbeddingCard />
    </QueryClientProvider>,
  );
}

describe("EmbeddingCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.getEmbeddingSettings.mockResolvedValue(settings());
  });

  it("shows where embeddings go, and the pinned model and dimension", async () => {
    renderCard();

    expect(await screen.findByText("http://env.test/v1")).toBeTruthy();
    expect(screen.getByText(/nomic-embed-text/)).toBeTruthy();
    expect(screen.getByText(/768/)).toBeTruthy();
    expect(screen.getByText(/server configuration/i)).toBeTruthy();
  });

  it("warns when embeddings are only following the default model", async () => {
    // Issue #836: this is the state that silently broke indexing when the default model moved.
    api.getEmbeddingSettings.mockResolvedValue(
      settings({ source: "DefaultModel", effectiveApiBase: "http://chat.test/v1", serverApiBase: null }),
    );

    renderCard();

    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toMatch(/default model/i);
  });

  it("warns when there is no endpoint at all", async () => {
    api.getEmbeddingSettings.mockResolvedValue(
      settings({ source: "None", effectiveApiBase: null, serverApiBase: null }),
    );

    renderCard();

    expect((await screen.findByRole("alert")).textContent).toMatch(/keyword/i);
  });

  it("does not warn when the endpoint is set on purpose", async () => {
    renderCard();
    await screen.findByText("http://env.test/v1");

    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("saves the endpoint, keeps the saved key when the key box is left blank, and says what was queued", async () => {
    api.getEmbeddingSettings.mockResolvedValue(settings({ savedHasApiKey: true }));
    api.saveEmbeddingSettings.mockResolvedValue({
      settings: settings({ source: "Platform", savedApiBase: "http://emb.test/v1", savedHasApiKey: true }),
      reindexQueued: 3,
    });

    renderCard();
    const endpoint = await screen.findByLabelText("Endpoint URL");
    fireEvent.change(endpoint, { target: { value: "http://emb.test/v1" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(api.saveEmbeddingSettings).toHaveBeenCalledWith({ apiBase: "http://emb.test/v1", apiKey: null }),
    );
    expect(await screen.findByText(/3 recordings queued/)).toBeTruthy();
  });

  it("sends a typed key", async () => {
    api.saveEmbeddingSettings.mockResolvedValue({ settings: settings(), reindexQueued: 0 });

    renderCard();
    fireEvent.change(await screen.findByLabelText("Endpoint URL"), { target: { value: "http://emb.test/v1" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "sk-1" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(api.saveEmbeddingSettings).toHaveBeenCalledWith({ apiBase: "http://emb.test/v1", apiKey: "sk-1" }),
    );
  });

  it("clearing the saved endpoint hands control back to the server setting", async () => {
    api.getEmbeddingSettings.mockResolvedValue(
      settings({ source: "Platform", savedApiBase: "http://emb.test/v1", effectiveApiBase: "http://emb.test/v1" }),
    );
    api.saveEmbeddingSettings.mockResolvedValue({ settings: settings(), reindexQueued: 0 });

    renderCard();
    fireEvent.click(await screen.findByRole("button", { name: "Clear" }));

    await waitFor(() => expect(api.saveEmbeddingSettings).toHaveBeenCalledWith({ apiBase: null, apiKey: null }));
  });

  it("reports a failed test with the endpoint's status and reason", async () => {
    api.testEmbedding.mockResolvedValue({
      ok: false, apiBase: "http://chat.test/v1", model: "nomic-embed-text", expectedDimension: 768,
      dimension: null, statusCode: 404, error: "404 (Not Found): model not found", durationMs: 5,
    });

    renderCard();
    fireEvent.click(await screen.findByRole("button", { name: "Test" }));

    const result = await screen.findByTestId("embedding-test-result");
    expect(result.textContent).toMatch(/404/);
    expect(result.textContent).toMatch(/model not found/);
  });

  it("reports a passing test with the vector size", async () => {
    api.testEmbedding.mockResolvedValue({
      ok: true, apiBase: "http://env.test/v1", model: "nomic-embed-text", expectedDimension: 768,
      dimension: 768, statusCode: 200, error: null, durationMs: 42,
    });

    renderCard();
    fireEvent.click(await screen.findByRole("button", { name: "Test" }));

    const result = await screen.findByTestId("embedding-test-result");
    expect(result.textContent).toMatch(/768/);
    expect(result.textContent).toMatch(/42 ms/);
  });
});
