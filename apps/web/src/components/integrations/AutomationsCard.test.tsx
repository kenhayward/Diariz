import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../../lib/api", () => ({
  api: {
    getProfile: vi.fn(),
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
import { api } from "../../lib/api";
import AutomationsCard from "./AutomationsCard";

function Wrapped() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={qc}>
      <AutomationsCard />
    </QueryClientProvider>
  );
}

/// Creating moved into a dialog: the card body is the list of what you already have, which is what the
/// tab used to bury under nine checkboxes.
const openComposer = async () =>
  fireEvent.click(await screen.findByRole("button", { name: /new automation/i }));

/// A row's four buttons became one kebab. There is exactly one row in every fixture below.
const openKebab = () => fireEvent.click(screen.getByRole("button", { name: /actions for/i }));
const openKebabAsync = async () => fireEvent.click(await screen.findByRole("button", { name: /actions for/i }));

describe("AutomationsCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getProfile).mockResolvedValue({ webhooksEnabled: true } as never);
    vi.mocked(api.listWebhooks).mockResolvedValue([]);
    vi.mocked(api.listWebhookDeliveries).mockResolvedValue([]);
  });

  it("offers the AI output events alongside the transcription ones", async () => {
    render(<Wrapped />);
    await openComposer();
    // The four events added so an automation can fire when an AI output is ready, rather than
    // triggering on transcription and polling for the summary.
    expect(screen.getByRole("button", { name: /summary is ready/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /meeting minutes are ready/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /action items are ready/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /tags are ready/i })).toBeTruthy();
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
    await openComposer();
    fireEvent.click(screen.getByRole("button", { name: /finishes transcribing/i }));
    fireEvent.change(screen.getByLabelText(/destination url/i), { target: { value: "https://x/y" } });
    fireEvent.click(screen.getByRole("button", { name: /create automation/i }));
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
    await openComposer();
    fireEvent.click(screen.getByRole("button", { name: /finishes transcribing/i }));
    fireEvent.change(screen.getByLabelText(/destination url/i), { target: { value: "https://x/y" } });
    fireEvent.click(screen.getByRole("button", { name: /create automation/i }));
    expect(await screen.findByText("dz_whsec_x")).toBeTruthy();
  });

  it("surfaces an error message when creation fails", async () => {
    vi.mocked(api.createWebhook).mockRejectedValue(new Error("nope"));
    render(<Wrapped />);
    await openComposer();
    fireEvent.click(screen.getByRole("button", { name: /finishes transcribing/i }));
    fireEvent.change(screen.getByLabelText(/destination url/i), { target: { value: "https://x/y" } });
    fireEvent.click(screen.getByRole("button", { name: /create automation/i }));
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
    // Host and triggers read as one line now, rather than a host line over a row of chips - that chip
    // row per automation is what made a list of three taller than the screen.
    expect(screen.getByText(/hooks\.zapier\.com.*finishes transcribing/i)).toBeTruthy();
    expect(screen.getByText("Active")).toBeTruthy();
    expect(screen.getAllByText(/finishes transcribing/i).length).toBeGreaterThan(0);
  });

  it("shows a paused status with a resume action for an auto-disabled automation", async () => {
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
    fireEvent.click((openKebab(), screen.getByRole("menuitem", { name: /resume/i })));
    await waitFor(() =>
      expect(updateWebhook).toHaveBeenCalledWith(
        "2",
        expect.objectContaining({ isActive: true, name: "Broken hook", url: "https://example.com/hook" }),
      ),
    );
  });

  // The pause half of the same button. Deliberately reversible: the alternative a user had was Delete,
  // which destroys the signing secret and forces the receiving end to be reconfigured.
  it("pauses an active automation without deleting it", async () => {
    vi.mocked(api.listWebhooks).mockResolvedValue([
      {
        id: "3",
        name: "Working hook",
        url: "https://example.com/hook",
        eventTypes: ["recording.created"],
        isActive: true,
        consecutiveFailures: 0,
        disabledReason: null,
        lastDeliveryAt: null,
        lastStatus: null,
        createdAt: new Date().toISOString(),
        includeAttendeeContacts: true,
      },
    ]);
    const updateWebhook = vi.mocked(api.updateWebhook).mockResolvedValue({
      id: "3",
      name: "Working hook",
      url: "https://example.com/hook",
      eventTypes: ["recording.created"],
      isActive: false,
      consecutiveFailures: 0,
      disabledReason: null,
      lastDeliveryAt: null,
      lastStatus: null,
      createdAt: new Date().toISOString(),
    });
    render(<Wrapped />);
    fireEvent.click((await openKebabAsync(), screen.getByRole("menuitem", { name: /pause/i })));
    await waitFor(() =>
      expect(updateWebhook).toHaveBeenCalledWith(
        "3",
        // The whole record goes back, because the endpoint replaces rather than patches - losing
        // includeAttendeeContacts here would silently stop contact details being sent.
        expect.objectContaining({
          isActive: false,
          name: "Working hook",
          url: "https://example.com/hook",
          eventTypes: ["recording.created"],
          includeAttendeeContacts: true,
        }),
      ),
    );
  });

  // "Paused - check the URL" is right for an automation the server gave up on, and wrong for one the
  // user paused on purpose. The two are told apart by whether the server set a disabledReason.
  it("does not tell the user to check the URL when they paused it themselves", async () => {
    vi.mocked(api.listWebhooks).mockResolvedValue([
      {
        id: "4",
        name: "Deliberately off",
        url: "https://example.com/hook",
        eventTypes: ["recording.created"],
        isActive: false,
        consecutiveFailures: 0,
        disabledReason: null,
        lastDeliveryAt: null,
        lastStatus: null,
        createdAt: new Date().toISOString(),
      },
    ]);
    render(<Wrapped />);
    expect(await screen.findByText(/^paused$/i)).toBeTruthy();
    expect(screen.queryByText(/check the URL/i)).toBeNull();
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
    fireEvent.click((await openKebabAsync(), screen.getByRole("menuitem", { name: /send test/i })));
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
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<Wrapped />);
    fireEvent.click((await openKebabAsync(), screen.getByRole("menuitem", { name: /delete/i })));
    expect(confirm).toHaveBeenCalled();
    await waitFor(() => expect(deleteWebhook).toHaveBeenCalledWith("1"));
    confirm.mockRestore();
  });

  /// Deleting destroys the signing secret, so the receiving end has to be reconfigured from scratch -
  /// it must not be one stray click from a menu.
  it("does not delete when the confirmation is declined", async () => {
    vi.mocked(api.listWebhooks).mockResolvedValue([
      {
        id: "1",
        name: "Doomed",
        url: "https://example.com/hook",
        eventTypes: ["recording.created"],
        isActive: true,
        consecutiveFailures: 0,
        disabledReason: null,
        lastDeliveryAt: null,
        lastStatus: null,
        createdAt: new Date().toISOString(),
      },
    ] as never);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    render(<Wrapped />);
    fireEvent.click((await openKebabAsync(), screen.getByRole("menuitem", { name: /delete/i })));

    expect(confirm).toHaveBeenCalled();
    expect(api.deleteWebhook).not.toHaveBeenCalled();
    confirm.mockRestore();
  });

  // Appended to the meta line the count was simply truncated off, which is the one number the header was
  // meant to give you at a glance.
  it("counts its automations in the card's chip, not on the end of the meta line", async () => {
    vi.mocked(api.listWebhooks).mockResolvedValue([
      {
        id: "1", name: "My Zap", url: "https://hooks.zapier.com/abc", eventTypes: ["recording.transcribed"],
        isActive: true, consecutiveFailures: 0, disabledReason: null, lastDeliveryAt: null, lastStatus: null,
        createdAt: new Date().toISOString(),
      },
    ] as never);
    render(<Wrapped />);

    expect(await screen.findByTestId("source-status")).toHaveProperty("textContent", "1 automation");
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
    await openComposer();
    fireEvent.click(screen.getByRole("button", { name: /formula finishes/i }));
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
    fireEvent.click((await openKebabAsync(), screen.getByRole("menuitem", { name: /recent deliveries/i })));
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
    expect(await screen.findByText(/too many failures/)).toBeTruthy();
  });
});
