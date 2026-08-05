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
    // The list shows every release version + headline; the detail also shows the newest's headline.
    //
    // Collect the rendered versions in ONE DOM traversal, then check membership. This used to call
    // getAllByText once per release, and since each of those walks the whole tree - a tree that itself
    // grows by one entry per release - the cost was quadratic in the number of releases shipped. It had
    // reached ~5s locally and was intermittently blowing CI's 20s per-test timeout, which would only have
    // got worse with every release. Same guarantee, linear cost.
    const rendered = new Set(screen.getAllByText(/^v\d+\.\d+\.\d+$/).map((el) => el.textContent));
    for (const r of RELEASES) expect(rendered).toContain(`v${r.version}`);
    expect(screen.getAllByText(newest.headline).length).toBeGreaterThan(0);
  });

  it("selecting a release shows its notes (with a PR link when present)", () => {
    render(<ReleaseNotes />);
    const target = RELEASES[0];

    // Click the list entry (first matching button) to select it.
    fireEvent.click(screen.getAllByText(`v${target.version}`)[0]);
    expect(screen.getAllByText(target.headline).length).toBeGreaterThan(0);

    if (target.pr != null) {
      const link = screen.getByRole("link", { name: `#${target.pr}` });
      expect(link.getAttribute("href")).toContain(`/pull/${target.pr}`);
      expect(link.getAttribute("target")).toBe("_blank");
    }
  });
});
