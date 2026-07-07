import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../auth", () => ({
  useAuth: () => ({ initials: "JD", pictureUrl: null, fullName: "Jane Doe", email: "jane@x.com" }),
}));
vi.mock("../lib/api", () => ({ api: { getProfile: vi.fn() } }));
// Isolate the shell: each tab's content is a simple marker.
vi.mock("./ProfileSection", () => ({ default: () => <div>PROFILE_SECTION</div> }));
vi.mock("./GoogleAccountSection", () => ({ default: () => <div>GOOGLE_SECTION</div> }));
vi.mock("./CalendarFeedsSection", () => ({ default: () => <div>FEEDS_SECTION</div> }));
vi.mock("./McpAccessSection", () => ({ default: () => <div>CLAUDE_SECTION</div> }));
vi.mock("./DeveloperAccessSection", () => ({ default: () => <div>DEVELOPERS_SECTION</div> }));
vi.mock("./VoicePrintsSection", () => ({ default: () => <div>VOICEPRINTS_SECTION</div> }));

import { api } from "../lib/api";
import PreferencesModal, { type PreferencesTab } from "./PreferencesModal";

function renderModal(props: { onClose?: () => void; initialTab?: PreferencesTab } = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <PreferencesModal onClose={props.onClose ?? (() => {})} initialTab={props.initialTab} />
    </QueryClientProvider>,
  );
}

describe("PreferencesModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ apiAccessEnabled: false });
  });

  it("renders the standard tabs headed by the user's name, showing Profile first", () => {
    renderModal();

    expect(screen.getByText("Jane Doe")).toBeTruthy();
    for (const name of [/profile/i, /google account/i, /calendar feeds/i, /claude access/i, /voice prints/i])
      expect(screen.getByRole("tab", { name })).toBeTruthy();
    expect(screen.getByText("PROFILE_SECTION")).toBeTruthy();
  });

  it("hides the Developers tab when API access is disabled, shows it when enabled", async () => {
    renderModal();
    // Disabled by default (getProfile → apiAccessEnabled:false): no Developers tab.
    expect(screen.queryByRole("tab", { name: /developers/i })).toBeNull();

    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ apiAccessEnabled: true });
    renderModal();
    expect(await screen.findByRole("tab", { name: /developers/i })).toBeTruthy();
  });

  it("switches the content panel when another tab is selected", () => {
    renderModal();
    fireEvent.click(screen.getByRole("tab", { name: /voice prints/i }));
    expect(screen.getByText("VOICEPRINTS_SECTION")).toBeTruthy();
    expect(screen.queryByText("PROFILE_SECTION")).toBeNull();
  });

  it("honours initialTab", () => {
    renderModal({ initialTab: "google" });
    expect(screen.getByText("GOOGLE_SECTION")).toBeTruthy();
    expect(screen.getByRole("tab", { name: /google account/i }).getAttribute("aria-selected")).toBe("true");
  });

  it("is sized to 60vw x 80vh", () => {
    renderModal();
    const dialog = screen.getByRole("dialog", { name: /preferences/i });
    expect(dialog.className).toContain("w-[60vw]");
    expect(dialog.className).toContain("h-[80vh]");
  });

  it("does not close on a backdrop click, but Close does", () => {
    const onClose = vi.fn();
    const { container } = renderModal({ onClose });
    fireEvent.click(container.firstChild as Element); // the backdrop overlay
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: /close/i }));
    expect(onClose).toHaveBeenCalled();
  });
});
