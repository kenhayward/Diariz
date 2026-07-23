import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { createWebhook: vi.fn() },
  apiErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
}));
import { api } from "../lib/api";
import AutomationsSection from "./AutomationsSection";

function Wrapped() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={qc}>
      <AutomationsSection />
    </QueryClientProvider>
  );
}

describe("AutomationsSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("creates an automation with the chosen event and url", async () => {
    const createWebhook = vi.mocked(api.createWebhook).mockResolvedValue({
      id: "1",
      name: "n",
      url: "https://x/y",
      eventTypes: ["recording.transcribed"],
      secret: "dz_whsec_x",
    });
    render(<Wrapped />);
    fireEvent.click(await screen.findByLabelText(/finishes transcribing/i));
    fireEvent.change(screen.getByLabelText(/destination url/i), { target: { value: "https://x/y" } });
    fireEvent.click(screen.getByRole("button", { name: /create|save/i }));
    await waitFor(() =>
      expect(createWebhook).toHaveBeenCalledWith(
        expect.objectContaining({ url: "https://x/y", eventTypes: ["recording.transcribed"] }),
      ),
    );
  });

  it("shows the signing secret once after creating", async () => {
    vi.mocked(api.createWebhook).mockResolvedValue({
      id: "1",
      name: "n",
      url: "https://x/y",
      eventTypes: ["recording.transcribed"],
      secret: "dz_whsec_x",
    });
    render(<Wrapped />);
    fireEvent.click(await screen.findByLabelText(/finishes transcribing/i));
    fireEvent.change(screen.getByLabelText(/destination url/i), { target: { value: "https://x/y" } });
    fireEvent.click(screen.getByRole("button", { name: /create|save/i }));
    expect(await screen.findByText("dz_whsec_x")).toBeTruthy();
  });

  it("surfaces an error message when creation fails", async () => {
    vi.mocked(api.createWebhook).mockRejectedValue(new Error("nope"));
    render(<Wrapped />);
    fireEvent.click(await screen.findByLabelText(/finishes transcribing/i));
    fireEvent.change(screen.getByLabelText(/destination url/i), { target: { value: "https://x/y" } });
    fireEvent.click(screen.getByRole("button", { name: /create|save/i }));
    expect(await screen.findByText("nope")).toBeTruthy();
  });
});
