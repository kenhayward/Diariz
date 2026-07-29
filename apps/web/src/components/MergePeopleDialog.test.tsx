import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import MergePeopleDialog from "./MergePeopleDialog";
import type { Person } from "../lib/types";

vi.mock("./HelpButton", () => ({
  default: ({ topic }: { topic: string }) => <span data-testid={`help-${topic}`} />,
}));

function person(id: string, name: string, over: Partial<Person> = {}): Person {
  return {
    id, name, title: null, companyName: null, email: null, phone: null,
    isInternal: false, voiceprintOptOut: false, hasVoiceprint: false, sampleCount: 0,
    linkedUserId: null, isSelf: false, canManageBiometrics: true,
    createdAt: "2026-07-29T00:00:00Z", updatedAt: "2026-07-29T00:00:00Z", ...over,
  };
}

/// The pair that prompted this: an enrolled voiceprint with nothing but a name, beside the account person of
/// the same human. Merging is right, but which way round it goes decides what survives - so the dialog has to
/// say so rather than leave it to a one-line confirm.
const enrolled = person("p1", "Ken Hayward", { hasVoiceprint: true, sampleCount: 5 });
const account = person("p2", "Ken hayward", {
  email: "ken@example.com", linkedUserId: "u1", title: "Director",
});

const onMerge = vi.fn();
const onClose = vi.fn();

function render_(a: Person = enrolled, b: Person = account) {
  render(<MergePeopleDialog people={[a, b]} reason="name" onMerge={onMerge} onClose={onClose} />);
}

beforeEach(() => {
  vi.clearAllMocks();
  onMerge.mockResolvedValue(undefined);
});

describe("MergePeopleDialog", () => {
  it("names which record is kept and which is deleted", () => {
    render_();

    expect(screen.getByText(/Keep .*Ken Hayward/)).toBeTruthy();
    expect(screen.getByText(/Delete .*Ken hayward/)).toBeTruthy();
  });

  it("says how many voice samples move, and where", () => {
    // Direction reversed, so the samples are the ones that have to travel.
    render_(account, enrolled);

    expect(screen.getByText(/5 samples/)).toBeTruthy();
    expect(screen.getByText(/5 samples/).textContent).toContain("Ken hayward");
  });

  it("says nothing about voice samples when neither has any", () => {
    render_(person("a", "Alice"), person("b", "Alice B"));

    expect(screen.queryByText(/samples/)).toBeNull();
  });

  it("lists only the details the surviving record will gain", () => {
    render_();

    // The survivor has no email or job title, so it picks both up. It has no company either, but neither
    // does the other record, so there is nothing to say about it.
    const gains = screen.getByTestId("merge-gains").textContent ?? "";
    expect(gains).toContain("Email address");
    expect(gains).toContain("Job title");
    expect(gains).not.toContain("Company");
  });

  it("warns that the surviving record becomes the account's person", () => {
    render_();

    expect(screen.getByText(/becomes the person for that Diariz account/i)).toBeTruthy();
  });

  it("swaps the direction, and everything it says with it", () => {
    render_();

    fireEvent.click(screen.getByRole("button", { name: /swap/i }));

    expect(screen.getByText(/Keep .*Ken hayward/)).toBeTruthy();
    expect(screen.getByText(/Delete .*Ken Hayward/)).toBeTruthy();
  });

  it("merges the source into the target, in that order", async () => {
    render_();

    fireEvent.click(screen.getByRole("button", { name: "Merge" }));

    await waitFor(() => expect(onMerge).toHaveBeenCalledWith("p1", "p2"));
  });

  it("merges the other way round after a swap", async () => {
    render_();

    fireEvent.click(screen.getByRole("button", { name: /swap/i }));
    fireEvent.click(screen.getByRole("button", { name: "Merge" }));

    await waitFor(() => expect(onMerge).toHaveBeenCalledWith("p2", "p1"));
  });

  it("does nothing on cancel", () => {
    render_();

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(onMerge).not.toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  /// The server refuses this with a 400. Saying so up front beats letting someone read an explanation only
  /// after they have committed to an irreversible action.
  it("refuses two records that each have an account, and offers no merge button", () => {
    render_(person("a", "Ken", { linkedUserId: "u1" }), person("b", "Ken", { linkedUserId: "u2" }));

    expect(screen.getByText(/two different people/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Merge" })).toBeNull();
  });

  /// The dialog says what happens to *this pair*; the article says what merging is and how to think about
  /// the direction. Pointing at the merge-specific article rather than the directory one matters, because the
  /// popover shows that article's summary - the generic one would answer a question nobody asked here.
  it("offers the merge help article rather than explaining everything inline", () => {
    render_();

    expect(screen.getByTestId("help-merging-people")).toBeTruthy();
  });
});
