import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
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

  // ---- The empty state ----
  //
  // Most identified people carry nothing but a name, because enrolling a voice is all it takes to create one.
  // The card used to render name-and-marker for those - the same two things already on the row above it, in a
  // thin box that reads as nothing at all. Saying the details are missing, and offering to add them, is the
  // difference between a useless card and a useful one.

  const bare = () => speaker({ title: null, companyName: null, email: null, phone: null });

  it("says so when a known person has no contact details", () => {
    render(<SpeakerContactCard info={bare()} />);

    expect(screen.getByText(/No contact details/i)).toBeTruthy();
  });

  it("offers to add them when the viewer may edit people", () => {
    const onEdit = vi.fn();
    render(<SpeakerContactCard info={bare()} canManagePeople onEdit={onEdit} />);

    fireEvent.click(screen.getByRole("button", { name: /Add details/i }));

    expect(onEdit).toHaveBeenCalled();
  });

  it("does not offer it without the permission", () => {
    render(<SpeakerContactCard info={bare()} onEdit={() => {}} />);

    expect(screen.queryByRole("button", { name: /Add details/i })).toBeNull();
    expect(screen.getByText(/No contact details/i)).toBeTruthy();
  });

  it("says nothing about missing details once there is something to show", () => {
    render(<SpeakerContactCard info={speaker()} canManagePeople onEdit={() => {}} />);

    expect(screen.queryByText(/No contact details/i)).toBeNull();
  });
});

/// The chip's tooltip and the card read from one function on purpose - two renderings of the same person
/// that could disagree is exactly the kind of drift nobody notices.
describe("contactSummary", () => {
  /// Repeating the name and the marker the row already shows is what made the tooltip pointless. When there
  /// is nothing to add, say that instead.
  it("says the details are missing rather than echoing the row", () => {
    const bare = speaker({ title: null, companyName: null, email: null, phone: null });

    expect(contactSummary(bare)).toBe("Lizzie Mcneil\nNo contact details recorded");
  });

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

  it("is the name plus the missing-details line when nothing else is known", () => {
    const bare = speaker({ title: null, companyName: null, email: null, phone: null, isInternal: null });
    expect(contactSummary(bare)).toBe("Lizzie Mcneil\nNo contact details recorded");
  });
});
