import { render, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { describe, it, expect } from "vitest";
import EpochDetail from "./EpochDetail";
import { EPOCHS, ARCHIVED_SPINE } from "../lib/releaseNotes/epochs";
import { ARCHIVE } from "../lib/releaseNotes/archive";
import { RECENT } from "../lib/releaseNotes";
import { epochSpan } from "../lib/releaseNotes/epochSpan";

const renderAt = (path: string) =>
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/release-notes" element={<div>epoch list</div>} />
        <Route path="/release-notes/:epochId" element={<EpochDetail />} />
      </Routes>
    </MemoryRouter>,
  );

const epoch = EPOCHS[0];

describe("EpochDetail", () => {
  it("lists every release in the epoch and no others", () => {
    renderAt(`/release-notes/${epoch.id}`);

    const shown = new Set(screen.getAllByText(/^v\d+\.\d+\.\d+$/).map((el) => el.textContent));
    const expected = ARCHIVE.slice(
      ARCHIVE.findIndex((r) => r.version === epoch.to),
      ARCHIVE.findIndex((r) => r.version === epoch.from) + 1,
    );

    for (const r of expected) expect(shown).toContain(`v${r.version}`);
    // The detail pane repeats the selected release's version, so the list is the set plus at most that.
    expect(shown.size).toBe(epochSpan(epoch, ARCHIVED_SPINE)!.count);
  });

  it("shows the epoch's newest release by default, with a link to its PR", () => {
    renderAt(`/release-notes/${epoch.id}`);
    const newest = ARCHIVE.find((r) => r.version === epoch.to)!;

    expect(screen.getAllByText(newest.headline).length).toBeGreaterThan(0);
    if (newest.pr != null) {
      const link = screen.getByRole("link", { name: `#${newest.pr}` });
      expect(link.getAttribute("href")).toContain(`/pull/${newest.pr}`);
    }
  });

  it("selecting a release shows its notes", () => {
    renderAt(`/release-notes/${epoch.id}`);
    const oldest = ARCHIVE.find((r) => r.version === epoch.from)!;

    fireEvent.click(screen.getAllByText(`v${oldest.version}`)[0]);

    expect(screen.getAllByText(oldest.headline).length).toBeGreaterThan(0);
  });

  it("serves the open epoch under 'current', reading the releases that have no epoch yet", () => {
    renderAt("/release-notes/current");

    const shown = new Set(screen.getAllByText(/^v\d+\.\d+\.\d+$/).map((el) => el.textContent));
    for (const r of RECENT) expect(shown).toContain(`v${r.version}`);
    expect(shown.size).toBe(RECENT.length);
  });

  it("sends an unknown epoch back to the list rather than rendering an empty page", () => {
    renderAt("/release-notes/no-such-epoch");
    expect(screen.getByText("epoch list")).toBeTruthy();
  });
});
