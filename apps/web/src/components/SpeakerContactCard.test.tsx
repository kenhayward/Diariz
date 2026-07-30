import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import SpeakerContactCard, { contactSummary } from "./SpeakerContactCard";
import type { SpeakerInfo } from "../lib/types";

function speaker(over: Partial<SpeakerInfo> = {}): SpeakerInfo {
  return {
    label: "SPEAKER_00", displayName: "Lizzie Mcneil", personId: "p1",
    title: "Presenter", companyName: "BBC", email: "lizzie@bbc.test", phone: "020 7946 0000",
    isInternal: false, identifiedAuto: true, isMultiSpeaker: false, ...over,
  };
}

describe("SpeakerContactCard", () => {
  it("shows who the speaker is", () => {
    render(<SpeakerContactCard info={speaker()} />);

    expect(screen.getByText("Lizzie Mcneil")).toBeTruthy();
    expect(screen.getByText(/Presenter/)).toBeTruthy();
    expect(screen.getByText(/BBC/)).toBeTruthy();
  });

  /// The panel is the one place in a transcript where these are reachable, so they have to be actionable
  /// rather than text to copy out by hand.
  it("makes the email address a mail link", () => {
    render(<SpeakerContactCard info={speaker()} />);

    const link = screen.getByRole("link", { name: "lizzie@bbc.test" });
    expect(link.getAttribute("href")).toBe("mailto:lizzie@bbc.test");
  });

  it("makes the phone number a call link, with the spaces stripped from the href", () => {
    render(<SpeakerContactCard info={speaker()} />);

    const link = screen.getByRole("link", { name: "020 7946 0000" });
    // Displayed as typed so it stays readable; dialled without the spaces, which tel: does not tolerate.
    expect(link.getAttribute("href")).toBe("tel:02079460000");
  });

  it("omits a detail that is not known rather than showing an empty row", () => {
    render(<SpeakerContactCard info={speaker({ phone: null, companyName: null })} />);

    expect(screen.queryByText(/Phone/)).toBeNull();
    expect(screen.queryByText(/BBC/)).toBeNull();
    expect(screen.getByRole("link", { name: "lizzie@bbc.test" })).toBeTruthy();
  });

  /// An anonymous speaker and a "Multiple Speakers" slot are not a person. Rendering a card for either would
  /// claim more than Diariz knows.
  it.each([
    ["an unidentified speaker", speaker({ personId: null, title: null, companyName: null, email: null, phone: null, isInternal: null })],
    ["a multi-speaker slot", speaker({ isMultiSpeaker: true })],
  ])("renders nothing for %s", (_case, info) => {
    const { container } = render(<SpeakerContactCard info={info} />);

    expect(container.textContent).toBe("");
  });

  it("renders nothing when the person is known but carries no details at all", () => {
    const bare = speaker({ title: null, companyName: null, email: null, phone: null, isInternal: null });
    const { container } = render(<SpeakerContactCard info={bare} />);

    expect(container.textContent).toBe("");
  });
});

/// The chip's tooltip and the card read from one function on purpose - two renderings of the same person
/// that could disagree is exactly the kind of drift nobody notices.
describe("contactSummary", () => {
  it("lists every detail that is known, one per line", () => {
    expect(contactSummary(speaker())).toBe(
      "Lizzie Mcneil\nPresenter\nBBC\nlizzie@bbc.test\n020 7946 0000\nExternal",
    );
  });

  it("skips what is not known", () => {
    expect(contactSummary(speaker({ title: null, phone: null, isInternal: null }))).toBe(
      "Lizzie Mcneil\nBBC\nlizzie@bbc.test",
    );
  });

  it("is just the name when nothing else is known", () => {
    const bare = speaker({ title: null, companyName: null, email: null, phone: null, isInternal: null });
    expect(contactSummary(bare)).toBe("Lizzie Mcneil");
  });
});
