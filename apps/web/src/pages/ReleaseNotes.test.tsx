import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect } from "vitest";
import ReleaseNotes from "./ReleaseNotes";
import { EPOCHS, ARCHIVED_SPINE } from "../lib/releaseNotes/epochs";
import { RECENT } from "../lib/releaseNotes";
import { epochSpan } from "../lib/releaseNotes/epochSpan";

const renderPage = () =>
  render(
    <MemoryRouter>
      <ReleaseNotes />
    </MemoryRouter>,
  );

describe("ReleaseNotes", () => {
  it("renders the fixed header", () => {
    renderPage();
    expect(screen.getByRole("heading", { name: /release notes/i })).toBeTruthy();
    expect(screen.getByText("Diariz")).toBeTruthy();
    expect(screen.getByText(/smart meeting transcription/i)).toBeTruthy();
  });

  it("leads with the current releases rather than an epoch", () => {
    renderPage();
    const latest = screen.getByRole("link", { name: /latest releases/i });
    expect(latest.getAttribute("href")).toBe("/release-notes/current");
    // The newest release's headline is the first thing a reader should see.
    expect(screen.getAllByText(RECENT[0].headline).length).toBeGreaterThan(0);
  });

  it("shows every epoch as a card linking to its own page", () => {
    renderPage();
    // One traversal, then membership - the same reason the old flat list stopped querying per entry:
    // a query per epoch walks a tree that grows with every epoch shipped.
    const hrefs = new Set(
      screen
        .getAllByRole("link")
        .map((a) => a.getAttribute("href"))
        .filter((h): h is string => h != null),
    );
    for (const e of EPOCHS) expect(hrefs).toContain(`/release-notes/${e.id}`);
    expect(hrefs.size).toBe(EPOCHS.length + 1); // + the current-releases card
  });

  it("tells the reader how big each epoch is, without loading the archive", () => {
    renderPage();
    for (const e of EPOCHS) {
      const span = epochSpan(e, ARCHIVED_SPINE);
      expect(span).not.toBeNull();
      expect(screen.getAllByText(e.title).length).toBeGreaterThan(0);
      expect(screen.getAllByText(new RegExp(`${span!.count} releases`)).length).toBeGreaterThan(0);
    }
  });
});
