import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    getProfile: vi.fn(),
    listCalendars: vi.fn(),
    saveCalendarSelection: vi.fn(),
    connectGoogle: vi.fn(),
    disconnectGoogle: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import GoogleAccountSection from "./GoogleAccountSection";

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;
const render_ = () => {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <GoogleAccountSection />
    </QueryClientProvider>,
  );
};

describe("GoogleAccountSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.getProfile).mockResolvedValue({
      email: "jane@x.com", fullName: "Jane", nativeLanguage: null, uiLanguage: null,
      googleConnected: true, googleCalendar: true,
      jobTitle: null, companyName: null, jobDescription: null, companyDescription: null, linkedIn: null, theme: "auto",
    });
    mock(api.listCalendars).mockResolvedValue([
      { id: "primary", summary: "Me", backgroundColor: "#ff0000", primary: true, selected: true },
      { id: "team", summary: "Team", backgroundColor: "#00ff00", primary: false, selected: false },
    ]);
    mock(api.saveCalendarSelection).mockResolvedValue(undefined);
  });

  it("lists the user's calendars with a colour swatch and their current selection", async () => {
    render_();
    const primary = (await screen.findByLabelText("Me")) as HTMLInputElement;
    const team = screen.getByLabelText("Team") as HTMLInputElement;
    expect(primary.checked).toBe(true);
    expect(team.checked).toBe(false);
    // A colour swatch is rendered (inline background style).
    expect(document.querySelector('[style*="background"]')).toBeTruthy();
  });

  it("saves the chosen calendar ids", async () => {
    render_();
    fireEvent.click(await screen.findByLabelText("Team")); // add Team to the selection
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.saveCalendarSelection).toHaveBeenCalledWith(expect.arrayContaining(["primary", "team"])),
    );
  });
});
