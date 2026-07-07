import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({ api: { getOpenApiDocument: vi.fn() } }));
vi.mock("@scalar/api-reference-react", () => ({
  ApiReferenceReact: ({ configuration }: { configuration: { content: unknown } }) => (
    <div data-testid="scalar">{configuration.content ? "HAS_SPEC" : "NO_SPEC"}</div>
  ),
}));
import { api } from "../lib/api";
import ApiReference from "./ApiReference";

function renderIt() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <ApiReference />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("ApiReference", () => {
  beforeEach(() => vi.clearAllMocks());

  it("fetches the OpenAPI document and renders Scalar with it", async () => {
    (api.getOpenApiDocument as ReturnType<typeof vi.fn>).mockResolvedValue({ openapi: "3.1.0", info: {} });
    renderIt();
    await waitFor(() => expect(api.getOpenApiDocument).toHaveBeenCalled());
    expect(await screen.findByText("HAS_SPEC")).toBeTruthy();
  });
});
