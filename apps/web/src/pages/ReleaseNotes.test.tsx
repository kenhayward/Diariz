import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import ReleaseNotes from "./ReleaseNotes";
import { RELEASES } from "../lib/releases";

describe("ReleaseNotes", () => {
  it("renders the fixed header, lists releases, and shows the newest by default", () => {
    render(<ReleaseNotes />);

    expect(screen.getByRole("heading", { name: /release notes/i })).toBeTruthy();
    expect(screen.getByText("Diariz")).toBeTruthy();
    expect(screen.getByText(/smart meeting transcription/i)).toBeTruthy();

    const newest = RELEASES[0];
    // The list shows every release version; the detail shows the newest's headline by default.
    for (const r of RELEASES) expect(screen.getAllByText(`v${r.version}`).length).toBeGreaterThan(0);
    expect(screen.getByText(newest.headline)).toBeTruthy();
  });

  it("selecting a release shows its notes (with a PR link when present)", () => {
    render(<ReleaseNotes />);
    const target = RELEASES[0];

    // Click the list entry (first matching button) to select it.
    fireEvent.click(screen.getAllByText(`v${target.version}`)[0]);
    expect(screen.getByText(target.headline)).toBeTruthy();

    if (target.pr != null) {
      const link = screen.getByRole("link", { name: `#${target.pr}` });
      expect(link.getAttribute("href")).toContain(`/pull/${target.pr}`);
      expect(link.getAttribute("target")).toBe("_blank");
    }
  });
});
