import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { SpeakerRow } from "./RecordingDetail";
import type { SpeakerInfo, SpeakerProfile } from "../lib/types";

const profiles: SpeakerProfile[] = [
  { id: "p1", name: "Alice", sampleCount: 1 },
  { id: "p2", name: "Bob", sampleCount: 2 },
];

function row(
  info: SpeakerInfo | undefined,
  handlers: Partial<{
    onRename: (n: string) => void;
    onAssign: (id: string | null) => void;
    onCreate: (name: string) => void;
    onMulti: () => void;
    onTogglePlay: () => void;
    canPlay: boolean;
    playing: boolean;
  }> = {},
) {
  render(
    <SpeakerRow
      label="SPEAKER_00"
      info={info}
      initial={info?.displayName ?? "SPEAKER_00"}
      profiles={profiles}
      canPlay={handlers.canPlay ?? true}
      playing={handlers.playing ?? false}
      onTogglePlay={handlers.onTogglePlay ?? (() => {})}
      onRename={handlers.onRename ?? (() => {})}
      onAssign={handlers.onAssign ?? (() => {})}
      onCreate={handlers.onCreate ?? (() => {})}
      onMulti={handlers.onMulti ?? (() => {})}
    />,
  );
}

const speaker = (over: Partial<SpeakerInfo> = {}): SpeakerInfo => ({
  label: "SPEAKER_00", displayName: "SPEAKER_00", profileId: null,
  identifiedAuto: false, isMultiSpeaker: false, ...over,
});

function openAssign() {
  fireEvent.click(screen.getByRole("button", { name: "Assign SPEAKER_00 to a person" }));
  return screen.getByRole("combobox");
}

describe("SpeakerRow", () => {
  it("assigning via the typeahead picks the chosen person", () => {
    const onAssign = vi.fn();
    row(speaker(), { onAssign });

    openAssign();
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Bob" } });
    fireEvent.click(screen.getByRole("option", { name: "Bob" }));

    expect(onAssign).toHaveBeenCalledWith("p2");
  });

  it("the Unassign action passes null", () => {
    const onAssign = vi.fn();
    row(speaker({ displayName: "Alice", profileId: "p1", identifiedAuto: true }), { onAssign });

    openAssign();
    fireEvent.click(screen.getByRole("option", { name: "Unassigned" }));

    expect(onAssign).toHaveBeenCalledWith(null);
  });

  it("typing an unknown name and choosing Create enrols a new person", () => {
    const onAssign = vi.fn();
    const onCreate = vi.fn();
    row(speaker(), { onAssign, onCreate });

    openAssign();
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Carol" } });
    fireEvent.click(screen.getByRole("option", { name: /create "carol"/i }));

    expect(onCreate).toHaveBeenCalledWith("Carol");
    expect(onAssign).not.toHaveBeenCalled();
  });

  it("the Multiple Speakers action triggers the multi handler (not an assign)", () => {
    const onAssign = vi.fn();
    const onMulti = vi.fn();
    row(speaker(), { onAssign, onMulti });

    openAssign();
    fireEvent.click(screen.getByRole("option", { name: "Multiple Speakers" }));

    expect(onMulti).toHaveBeenCalledOnce();
    expect(onAssign).not.toHaveBeenCalled();
  });

  it("shows the speaker as Multiple Speakers on the assignment trigger when flagged", () => {
    row(speaker({ displayName: "Multiple Speakers", isMultiSpeaker: true }));
    const trigger = screen.getByRole("button", { name: "Assign SPEAKER_00 to a person" });
    expect(trigger.textContent).toContain("Multiple Speakers");
  });

  it("shows an auto badge only when identified automatically", () => {
    row(speaker({ displayName: "Alice", profileId: "p1", identifiedAuto: true }));
    expect(screen.getByText("auto")).toBeTruthy();
  });

  it("hides the auto badge for a manually-named speaker", () => {
    row(speaker({ displayName: "Carol" }));
    expect(screen.queryByText("auto")).toBeNull();
  });

  it("renders a play control that toggles per-speaker playback", () => {
    const onTogglePlay = vi.fn();
    row(speaker({ displayName: "Alice" }), { onTogglePlay });
    fireEvent.click(screen.getByRole("button", { name: /play alice's segments/i }));
    expect(onTogglePlay).toHaveBeenCalledOnce();
  });

  it("hides the play control when the recording has no audio", () => {
    row(speaker({ displayName: "Alice" }), { canPlay: false });
    expect(screen.queryByRole("button", { name: /play .*segments/i })).toBeNull();
  });
});
