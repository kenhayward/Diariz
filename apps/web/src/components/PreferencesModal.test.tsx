import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../auth", () => ({
  useAuth: () => ({ initials: "JD", pictureUrl: null, fullName: "Jane Doe", email: "jane@x.com" }),
}));
vi.mock("../lib/api", () => ({ api: { getProfile: vi.fn() } }));
// Isolate the shell: each tab's content is a simple marker.
vi.mock("./ProfileSection", () => ({ default: () => <div>PROFILE_SECTION</div> }));
vi.mock("./AiSettingsSection", () => ({ default: () => <div>AI_SECTION</div> }));
vi.mock("./ChatToolsSection", () => ({ default: () => <div>TOOLS_SECTION</div> }));
vi.mock("./RecordingsSection", () => ({ default: () => <div>RECORDINGS_SECTION</div> }));
vi.mock("./FormulasSection", () => ({ default: () => <div>FORMULAS_SECTION</div> }));
vi.mock("./calendars/CalendarsSection", () => ({ default: () => <div>CALENDARS_SECTION</div> }));
vi.mock("./integrations/IntegrationsSection", () => ({ default: () => <div>INTEGRATIONS_SECTION</div> }));

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
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ apiAccessEnabled: false, webhooksEnabled: false });
  });

  it("renders the standard tabs headed by the user's name, showing Profile first", () => {
    renderModal();

    expect(screen.getByText("Jane Doe")).toBeTruthy();
    for (const name of [/profile/i, /^calendars$/i, /^integrations$/i])
      expect(screen.getByRole("tab", { name })).toBeTruthy();
    expect(screen.getByText("PROFILE_SECTION")).toBeTruthy();
  });

  it("carries the personal settings tabs moved out of Settings (Model Settings, Chat Tools, Recordings)", () => {
    renderModal();
    for (const name of [/model settings/i, /chat tools/i, /^recordings$/i])
      expect(screen.getByRole("tab", { name })).toBeTruthy();

    fireEvent.click(screen.getByRole("tab", { name: /model settings/i }));
    expect(screen.getByText("AI_SECTION")).toBeTruthy();
    fireEvent.click(screen.getByRole("tab", { name: /chat tools/i }));
    expect(screen.getByText("TOOLS_SECTION")).toBeTruthy();
    fireEvent.click(screen.getByRole("tab", { name: /^recordings$/i }));
    expect(screen.getByText("RECORDINGS_SECTION")).toBeTruthy();
  });

  it("has a Formulas tab that shows the Formulas section and honours initialTab", () => {
    renderModal({ initialTab: "formulas" });
    expect(screen.getByRole("tab", { name: /formulas/i }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByText("FORMULAS_SECTION")).toBeTruthy();
  });

  // The tab list used to grow and shrink with the platform's feature switches, so a user whose
  // administrator had turned API access off simply had no Developers tab and no way to know it existed.
  // The entries are fixed now and each card says on its own face what is switched off - see
  // ApiCard.test.tsx and AutomationsCard.test.tsx.
  it("keeps a fixed tab list whatever the platform has switched off", async () => {
    // Scoped to each render's own container: both modals share the document, so a global tab query would
    // see one list of twelve rather than two of six.
    const tabsIn = (c: HTMLElement) => [...c.querySelectorAll('[role="tab"]')].map((x) => x.textContent);

    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ apiAccessEnabled: false, webhooksEnabled: false });
    const off = renderModal();
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ apiAccessEnabled: true, webhooksEnabled: true });
    const on = renderModal();

    expect(tabsIn(on.container)).toEqual(tabsIn(off.container));
    expect(tabsIn(on.container)).toContain("Integrations");
  });

  it("switches the content panel when another tab is selected", () => {
    renderModal();
    // Voice Prints moved out to the top-level People page: a platform-wide directory is not a personal
    // preference, so it is no longer a tab here.
    fireEvent.click(screen.getByRole("tab", { name: /^calendars$/i }));
    expect(screen.getByText("CALENDARS_SECTION")).toBeTruthy();
    expect(screen.queryByText("PROFILE_SECTION")).toBeNull();
  });

  // Three nav entries - Google Account, Calendar Feeds and Outlook - became one. Answering "what feeds my
  // Calendar tab?" used to mean visiting three pages and reconciling three layouts.
  it("has one Calendars tab in place of the three calendar-source tabs", () => {
    renderModal();
    for (const gone of [/google account/i, /calendar feeds/i, /^outlook$/i])
      expect(screen.queryByRole("tab", { name: gone })).toBeNull();
    expect(screen.getByRole("tab", { name: /^calendars$/i })).toBeTruthy();
  });

  // Claude Access, Developers and Automations were the same kind of thing - how other software talks to
  // Diariz - and thin on their own.
  it("has one Integrations tab in place of the three technical tabs", () => {
    renderModal();
    for (const gone of [/claude access/i, /developers/i, /^automations$/i])
      expect(screen.queryByRole("tab", { name: gone })).toBeNull();

    fireEvent.click(screen.getByRole("tab", { name: /^integrations$/i }));
    expect(screen.getByText("INTEGRATIONS_SECTION")).toBeTruthy();
  });

  it("honours initialTab", () => {
    renderModal({ initialTab: "calendars" });
    expect(screen.getByText("CALENDARS_SECTION")).toBeTruthy();
    expect(screen.getByRole("tab", { name: /^calendars$/i }).getAttribute("aria-selected")).toBe("true");
  });

  it("is sized to 80vw x 80vh", () => {
    renderModal();
    const dialog = screen.getByRole("dialog", { name: /preferences/i });
    expect(dialog.className).toContain("w-[80vw]");
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
