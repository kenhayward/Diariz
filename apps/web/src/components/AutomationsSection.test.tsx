import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    createWebhook: vi.fn(),
    listWebhooks: vi.fn(),
    testWebhook: vi.fn(),
    deleteWebhook: vi.fn(),
    updateWebhook: vi.fn(),
    createApiToken: vi.fn(),
    listWebhookDeliveries: vi.fn(),
  },
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
    vi.mocked(api.listWebhooks).mockResolvedValue([]);
    vi.mocked(api.listWebhookDeliveries).mockResolvedValue([]);
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

  it("renders existing automations as cards with trigger chips and an active status", async () => {
    vi.mocked(api.listWebhooks).mockResolvedValue([
      {
        id: "1",
        name: "My Zap",
        url: "https://hooks.zapier.com/abc",
        eventTypes: ["recording.transcribed"],
        isActive: true,
        consecutiveFailures: 0,
        disabledReason: null,
        lastDeliveryAt: null,
        lastStatus: null,
        createdAt: new Date().toISOString(),
      },
    ]);
    render(<Wrapped />);
    expect(await screen.findByText("My Zap")).toBeTruthy();
    expect(screen.getByText("hooks.zapier.com")).toBeTruthy();
    expect(screen.getByText(/active/i)).toBeTruthy();
    expect(screen.getAllByText(/finishes transcribing/i).length).toBeGreaterThan(0);
  });

  it("shows a paused status with a re-enable action for an auto-disabled automation", async () => {
    vi.mocked(api.listWebhooks).mockResolvedValue([
      {
        id: "2",
        name: "Broken hook",
        url: "https://example.com/hook",
        eventTypes: ["recording.created"],
        isActive: false,
        consecutiveFailures: 5,
        disabledReason: "too many failures",
        lastDeliveryAt: null,
        lastStatus: "failed",
        createdAt: new Date().toISOString(),
      },
    ]);
    render(<Wrapped />);
    expect(await screen.findByText(/paused/i)).toBeTruthy();
    const updateWebhook = vi.mocked(api.updateWebhook).mockResolvedValue({
      id: "2",
      name: "Broken hook",
      url: "https://example.com/hook",
      eventTypes: ["recording.created"],
      isActive: true,
      consecutiveFailures: 0,
      disabledReason: null,
      lastDeliveryAt: null,
      lastStatus: null,
      createdAt: new Date().toISOString(),
    });
    fireEvent.click(screen.getByRole("button", { name: /re-enable/i }));
    await waitFor(() =>
      expect(updateWebhook).toHaveBeenCalledWith(
        "2",
        expect.objectContaining({ isActive: true, name: "Broken hook", url: "https://example.com/hook" }),
      ),
    );
  });

  it("sends a test event", async () => {
    vi.mocked(api.listWebhooks).mockResolvedValue([
      {
        id: "1",
        name: "n",
        url: "https://x/y",
        eventTypes: ["recording.transcribed"],
        isActive: true,
        consecutiveFailures: 0,
        disabledReason: null,
        lastDeliveryAt: null,
        lastStatus: null,
        createdAt: new Date().toISOString(),
      },
    ]);
    const testWebhook = vi.mocked(api.testWebhook).mockResolvedValue();
    render(<Wrapped />);
    fireEvent.click(await screen.findByRole("button", { name: /send test/i }));
    await waitFor(() => expect(testWebhook).toHaveBeenCalledWith("1"));
  });

  it("deletes an automation", async () => {
    vi.mocked(api.listWebhooks).mockResolvedValue([
      {
        id: "1",
        name: "n",
        url: "https://x/y",
        eventTypes: ["recording.transcribed"],
        isActive: true,
        consecutiveFailures: 0,
        disabledReason: null,
        lastDeliveryAt: null,
        lastStatus: null,
        createdAt: new Date().toISOString(),
      },
    ]);
    const deleteWebhook = vi.mocked(api.deleteWebhook).mockResolvedValue();
    render(<Wrapped />);
    fireEvent.click(await screen.findByRole("button", { name: /delete/i }));
    await waitFor(() => expect(deleteWebhook).toHaveBeenCalledWith("1"));
  });

  it("shows an empty state when there are no automations", async () => {
    render(<Wrapped />);
    expect(await screen.findByText(/no automations yet/i)).toBeTruthy();
  });

  it("offers a read-only token when a formula event is selected, and creates it on click", async () => {
    const createApiToken = vi.mocked(api.createApiToken).mockResolvedValue({
      id: "tok1",
      name: "Automation token",
      prefix: "dz_",
      token: "dz_plaintext_x",
    });
    render(<Wrapped />);
    fireEvent.click(await screen.findByLabelText(/formula finishes/i));
    expect(await screen.findByText(/read-only access token/i)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /create token/i }));
    await waitFor(() =>
      expect(createApiToken).toHaveBeenCalledWith(expect.any(String), { readOnly: true, expiresAt: null }),
    );
    expect(await screen.findByText("dz_plaintext_x")).toBeTruthy();
  });

  it("expands recent deliveries and renders a returned delivery row", async () => {
    vi.mocked(api.listWebhooks).mockResolvedValue([
      {
        id: "1",
        name: "n",
        url: "https://x/y",
        eventTypes: ["recording.transcribed"],
        isActive: true,
        consecutiveFailures: 0,
        disabledReason: null,
        lastDeliveryAt: null,
        lastStatus: null,
        createdAt: new Date().toISOString(),
      },
    ]);
    const listWebhookDeliveries = vi.mocked(api.listWebhookDeliveries).mockResolvedValue([
      {
        id: "d1",
        eventType: "recording.transcribed",
        status: "success",
        attemptCount: 1,
        responseStatus: 200,
        lastError: null,
        createdAt: new Date().toISOString(),
        nextAttemptAt: null,
      },
    ]);
    render(<Wrapped />);
    fireEvent.click(await screen.findByRole("button", { name: /recent deliveries/i }));
    await waitFor(() => expect(listWebhookDeliveries).toHaveBeenCalledWith("1"));
    expect(await screen.findByText(/success/)).toBeTruthy();
  });

  it("shows the disabled reason on a paused automation", async () => {
    vi.mocked(api.listWebhooks).mockResolvedValue([
      {
        id: "2",
        name: "Broken hook",
        url: "https://example.com/hook",
        eventTypes: ["recording.created"],
        isActive: false,
        consecutiveFailures: 5,
        disabledReason: "too many failures",
        lastDeliveryAt: null,
        lastStatus: "failed",
        createdAt: new Date().toISOString(),
      },
    ]);
    render(<Wrapped />);
    expect(await screen.findByText("too many failures")).toBeTruthy();
  });
});
