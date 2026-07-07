import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";

vi.mock("../auth", () => ({
  useAuth: () => ({ initials: "JD", pictureUrl: null, fullName: "Jane Doe", email: "jane@x.com" }),
}));
// Isolate the shell: each tab's content is a simple marker.
vi.mock("./ProfileSection", () => ({ default: () => <div>PROFILE_SECTION</div> }));
vi.mock("./GoogleAccountSection", () => ({ default: () => <div>GOOGLE_SECTION</div> }));
vi.mock("./CalendarFeedsSection", () => ({ default: () => <div>FEEDS_SECTION</div> }));
vi.mock("./McpAccessSection", () => ({ default: () => <div>CLAUDE_SECTION</div> }));
vi.mock("./VoicePrintsSection", () => ({ default: () => <div>VOICEPRINTS_SECTION</div> }));

import PreferencesModal from "./PreferencesModal";

describe("PreferencesModal", () => {
  it("renders the five tabs headed by the user's name, showing Profile first", () => {
    render(<PreferencesModal onClose={() => {}} />);

    expect(screen.getByText("Jane Doe")).toBeTruthy();
    for (const name of [/profile/i, /google account/i, /calendar feeds/i, /claude access/i, /voice prints/i])
      expect(screen.getByRole("tab", { name })).toBeTruthy();
    expect(screen.getByText("PROFILE_SECTION")).toBeTruthy();
  });

  it("switches the content panel when another tab is selected", () => {
    render(<PreferencesModal onClose={() => {}} />);
    fireEvent.click(screen.getByRole("tab", { name: /voice prints/i }));
    expect(screen.getByText("VOICEPRINTS_SECTION")).toBeTruthy();
    expect(screen.queryByText("PROFILE_SECTION")).toBeNull();
  });

  it("honours initialTab", () => {
    render(<PreferencesModal onClose={() => {}} initialTab="google" />);
    expect(screen.getByText("GOOGLE_SECTION")).toBeTruthy();
    expect(screen.getByRole("tab", { name: /google account/i }).getAttribute("aria-selected")).toBe("true");
  });

  it("is sized to 60vw x 80vh", () => {
    render(<PreferencesModal onClose={() => {}} />);
    const dialog = screen.getByRole("dialog", { name: /preferences/i });
    expect(dialog.className).toContain("w-[60vw]");
    expect(dialog.className).toContain("h-[80vh]");
  });

  it("does not close on a backdrop click, but Close does", () => {
    const onClose = vi.fn();
    const { container } = render(<PreferencesModal onClose={onClose} />);
    fireEvent.click(container.firstChild as Element); // the backdrop overlay
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: /close/i }));
    expect(onClose).toHaveBeenCalled();
  });
});
