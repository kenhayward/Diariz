import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

vi.mock("../../lib/api", () => ({
  api: {
    getProfile: vi.fn(),
    listCalendars: vi.fn(),
    saveCalendarSelection: vi.fn(),
    connectGoogle: vi.fn(),
    disconnectGoogle: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../../lib/api";
import GoogleCalendarCard from "./GoogleCalendarCard";

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

const profile = (over: Record<string, unknown> = {}) => ({
  email: "jane@x.com", fullName: "Jane", nativeLanguage: null, uiLanguage: null,
  googleConnected: true, googleCalendar: true,
  jobTitle: null, companyName: null, jobDescription: null, companyDescription: null, linkedIn: null, theme: "auto",
  ...over,
});

const renderCard = () => {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <GoogleCalendarCard />
    </QueryClientProvider>,
  );
};

describe("GoogleCalendarCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.getProfile).mockResolvedValue(profile());
    mock(api.listCalendars).mockResolvedValue([
      { id: "primary", summary: "Me", backgroundColor: "#ff0000", primary: true, selected: true },
      { id: "team", summary: "Team", backgroundColor: "#00ff00", primary: false, selected: false },
    ]);
    mock(api.saveCalendarSelection).mockResolvedValue(undefined);
    mock(api.connectGoogle).mockResolvedValue("https://accounts.google.example/consent");
    mock(api.disconnectGoogle).mockResolvedValue(undefined);
  });

  afterEach(() => vi.restoreAllMocks());

  it("lists the user's calendars with a colour swatch and their current selection", async () => {
    renderCard();
    const primary = (await screen.findByLabelText("Me")) as HTMLInputElement;
    const team = screen.getByLabelText("Team") as HTMLInputElement;
    expect(primary.checked).toBe(true);
    expect(team.checked).toBe(false);
    // A colour swatch is rendered (inline background style).
    expect(document.querySelector('[style*="background"]')).toBeTruthy();
  });

  // The explicit Save is gone: every other control on this panel saves on change, and three save models on
  // one screen was the readability problem the merge set out to fix.
  it("saves the chosen calendar ids as they are ticked, with no Save button", async () => {
    renderCard();
    fireEvent.click(await screen.findByLabelText("Team")); // add Team to the selection

    await waitFor(() =>
      expect(api.saveCalendarSelection).toHaveBeenCalledWith(expect.arrayContaining(["primary", "team"])),
    );
    expect(screen.queryByRole("button", { name: /^save$/i })).toBeNull();
  });

  it("counts the calendars, and how many are in use, in the card header", async () => {
    renderCard();
    expect(await screen.findByText(/jane@x\.com.*2 calendars, 1 in use/)).toBeTruthy();
    expect(screen.getByTestId("source-status").textContent).toBe("Connected");
  });

  // The scope tick is gone. It was a pre-connect request that disabled itself once granted and did nothing
  // afterwards; asking for the scope is what Reconnect is for.
  it("asks Google for calendar access when reconnecting, with no scope checkbox to tick first", async () => {
    const assign = vi.fn();
    vi.spyOn(window, "location", "get").mockReturnValue({ ...window.location, assign } as Location);
    renderCard();

    fireEvent.click(await screen.findByRole("button", { name: /reconnect/i }));

    await waitFor(() => expect(api.connectGoogle).toHaveBeenCalledWith({ calendar: true }));
    expect(screen.queryByRole("checkbox", { name: /read my google calendar/i })).toBeNull();
  });

  /// Signed in but with no calendar scope: there is nothing to list, so the body says what is missing and
  /// offers the one button that fixes it - and must not ask for calendars it cannot read.
  it("offers to grant access when Google is connected without the calendar scope", async () => {
    mock(api.getProfile).mockResolvedValue(profile({ googleCalendar: false }));
    renderCard();

    expect(await screen.findByRole("button", { name: /grant calendar access/i })).toBeTruthy();
    expect(api.listCalendars).not.toHaveBeenCalled();
  });

  /// Disconnect used to be hidden unless the calendar scope had been granted, which left anyone connected
  /// without it unable to disconnect at all.
  it("can disconnect whenever Google is connected, after confirming", async () => {
    mock(api.getProfile).mockResolvedValue(profile({ googleCalendar: false }));
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    renderCard();

    fireEvent.click(await screen.findByRole("button", { name: /disconnect/i }));

    expect(confirm).toHaveBeenCalled();
    await waitFor(() => expect(api.disconnectGoogle).toHaveBeenCalled());
  });

  it("does not disconnect when the confirmation is declined", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    renderCard();

    fireEvent.click(await screen.findByRole("button", { name: /disconnect/i }));

    expect(api.disconnectGoogle).not.toHaveBeenCalled();
  });

  it("says it is not connected, and offers no calendar list, when Google is not linked", async () => {
    mock(api.getProfile).mockResolvedValue(profile({ googleConnected: false, googleCalendar: false }));
    renderCard();

    expect(await screen.findByText(/not connected/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /disconnect/i })).toBeNull();
    expect(api.listCalendars).not.toHaveBeenCalled();
  });
});
