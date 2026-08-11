import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../auth", () => ({
  useAuth: () => ({ initials: "JD", pictureUrl: null, fullName: "Jane Doe", email: "jane@x.com" }),
}));
vi.mock("../lib/api", () => ({ api: { getProfile: vi.fn() } }));
// Isolate the shell: each tab's content is a simple marker.
vi.mock("./ProfileSection", () => ({ default: () => <div>PROFILE_SECTION</div> }));
// Always passes a registration object (never null) - it stands in for the tab once its data has loaded,
// which is the state PreferencesModal's own tests care about (switching tabs, footer wiring). The
// null-while-loading case is RecordingsSection's own concern and is covered in RecordingsSection.test.tsx.
vi.mock("./RecordingsSection", () => ({
  default: () => {
    usePreferencesFooter({ dirty: true, busy: false, status: "unsaved", error: null, onSave: () => {} });
    return <div>RECORDINGS_SECTION</div>;
  },
}));
vi.mock("./FormulasSection", () => ({ default: () => <div>FORMULAS_SECTION</div> }));
vi.mock("./calendars/CalendarsSection", () => ({ default: () => <div>CALENDARS_SECTION</div> }));
vi.mock("./integrations/IntegrationsSection", () => ({ default: () => <div>INTEGRATIONS_SECTION</div> }));
vi.mock("./assistant/AssistantSection", () => ({ default: () => <div>ASSISTANT_SECTION</div> }));

import { api } from "../lib/api";
import { usePreferencesFooter } from "./PreferencesFooter";
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
    for (const name of [/profile/i, /^calendars$/i, /^integrations$/i, /^assistant$/i])
      expect(screen.getByRole("tab", { name })).toBeTruthy();
    expect(screen.getByText("PROFILE_SECTION")).toBeTruthy();
  });

  // Model Settings and Chat Tools were two of the personal settings moved out of the admin Settings
  // modal; they are one Assistant tab now, under Advanced. Recordings stays where it was.
  it("carries the personal settings moved out of Settings, with the model and tools under Assistant", () => {
    renderModal();
    fireEvent.click(screen.getByRole("tab", { name: /^recordings$/i }));
    expect(screen.getByText("RECORDINGS_SECTION")).toBeTruthy();

    for (const gone of [/model settings/i, /chat tools/i])
      expect(screen.queryByRole("tab", { name: gone })).toBeNull();

    fireEvent.click(screen.getByRole("tab", { name: /^assistant$/i }));
    expect(screen.getByText("ASSISTANT_SECTION")).toBeTruthy();
  });

  /// The exception settings sit below a divider so the entries above read as the ones worth looking at.
  /// It is a label, not a control - there is nothing to expand.
  it("separates the advanced entries with a label, not a control", () => {
    renderModal();
    const advanced = screen.getByText("Advanced");
    expect(advanced).toBeTruthy();
    expect(advanced.closest("button")).toBeNull();

    // Everything after it in the nav is an exception setting.
    const labels = [...screen.getAllByRole("tab")].map((x) => x.textContent);
    expect(labels.slice(-2)).toEqual(["Integrations", "Assistant"]);
  });

  it("gives every nav entry a glyph", () => {
    renderModal();
    for (const tab of screen.getAllByRole("tab")) expect(tab.querySelector("svg")).not.toBeNull();
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

  it("shows Save changes only on a tab that opts into the footer", () => {
    renderModal();
    // Profile does not register.
    expect(screen.queryByRole("button", { name: /save changes/i })).toBeNull();

    fireEvent.click(screen.getByRole("tab", { name: /^recordings$/i }));
    expect(screen.getByRole("button", { name: /save changes/i })).toBeTruthy();
    expect(screen.getByText("Unsaved changes")).toBeTruthy();

    // Switching away must not leave a stale Save button behind pointing at an unmounted tab.
    fireEvent.click(screen.getByRole("tab", { name: /^profile$/i }));
    expect(screen.queryByRole("button", { name: /save changes/i })).toBeNull();
  });

  it("names the active tab in a breadcrumb beside the dialog title", () => {
    renderModal();
    expect(screen.getByText("/ Profile")).toBeTruthy();
    fireEvent.click(screen.getByRole("tab", { name: /^calendars$/i }));
    expect(screen.getByText("/ Calendars")).toBeTruthy();
  });
});
