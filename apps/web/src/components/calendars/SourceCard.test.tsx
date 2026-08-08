import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import { GlobeIcon } from "../icons";
import SourceCard from "./SourceCard";

/// The shell every calendar source shares. Its whole job is to make a fourth provider cheap, so what is
/// pinned here is the header grammar - tile, name, meta, status, actions - not any one provider's content.

function renderCard(props: Partial<Parameters<typeof SourceCard>[0]> = {}) {
  return render(
    <SourceCard
      name="Subscribed feeds (.ics)"
      meta="Public team calendars by URL"
      tint="#7986CB"
      glyph={GlobeIcon}
      {...props}
    >
      <p>BODY</p>
    </SourceCard>,
  );
}

describe("SourceCard", () => {
  it("heads the card with its name and meta line, over its body", () => {
    renderCard();
    expect(screen.getByText("Subscribed feeds (.ics)")).toBeTruthy();
    expect(screen.getByText("Public team calendars by URL")).toBeTruthy();
    expect(screen.getByText("BODY")).toBeTruthy();
  });

  // A card is a labelled region so a screen-reader user can tell which source a "Shown" tick belongs to -
  // the whole reason three sections on separate tabs could reuse the same control names.
  it("is a region named after its source", () => {
    renderCard();
    expect(screen.getByRole("region", { name: "Subscribed feeds (.ics)" })).toBeTruthy();
  });

  it("shows a status chip only when the source has something to report", () => {
    const { unmount } = renderCard();
    expect(screen.queryByTestId("source-status")).toBeNull();
    unmount();

    renderCard({ status: "Connected" });
    expect(screen.getByTestId("source-status").textContent).toBe("Connected");
  });

  it("puts a source's own actions in its header", () => {
    renderCard({ actions: <button type="button">Reconnect</button> });
    expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy();
  });

  // Each card renders its own errors: a panel-level banner could not say which source failed.
  it("renders a failure under the card it belongs to", () => {
    renderCard({ error: "Could not save that change." });
    expect(screen.getByText("Could not save that change.")).toBeTruthy();
  });

  it("tints its tile with the provider colour", () => {
    renderCard({ tint: "#0F6CBD" });
    expect(screen.getByTestId("source-tile").getAttribute("style")).toContain("15, 108, 189");
  });
});
