import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { SpeakerRow } from "./RecordingDetail";
import type { SpeakerInfo, SpeakerProfile } from "../lib/types";

const profiles: SpeakerProfile[] = [
  { id: "p1", name: "Alice", sampleCount: 1 },
  { id: "p2", name: "Bob", sampleCount: 2 },
];

function row(info: SpeakerInfo | undefined, handlers: Partial<{
  onRename: (n: string) => void;
  onAssign: (id: string | null) => void;
  onNewPerson: () => void;
}> = {}) {
  render(
    <SpeakerRow
      label="SPEAKER_00"
      info={info}
      initial={info?.displayName ?? "SPEAKER_00"}
      profiles={profiles}
      onRename={handlers.onRename ?? (() => {})}
      onAssign={handlers.onAssign ?? (() => {})}
      onNewPerson={handlers.onNewPerson ?? (() => {})}
    />,
  );
}

describe("SpeakerRow", () => {
  it("selecting a profile assigns the speaker to it", () => {
    const onAssign = vi.fn();
    row({ label: "SPEAKER_00", displayName: "SPEAKER_00", profileId: null, identifiedAuto: false }, { onAssign });

    fireEvent.change(screen.getByLabelText("Assign SPEAKER_00 to a person"), { target: { value: "p2" } });

    expect(onAssign).toHaveBeenCalledWith("p2");
  });

  it("selecting Unassigned passes null", () => {
    const onAssign = vi.fn();
    row({ label: "SPEAKER_00", displayName: "Alice", profileId: "p1", identifiedAuto: true }, { onAssign });

    fireEvent.change(screen.getByLabelText("Assign SPEAKER_00 to a person"), { target: { value: "" } });

    expect(onAssign).toHaveBeenCalledWith(null);
  });

  it("choosing + New person triggers enrolment (not an assign)", () => {
    const onAssign = vi.fn();
    const onNewPerson = vi.fn();
    row({ label: "SPEAKER_00", displayName: "SPEAKER_00", profileId: null, identifiedAuto: false }, { onAssign, onNewPerson });

    fireEvent.change(screen.getByLabelText("Assign SPEAKER_00 to a person"), { target: { value: "__new__" } });

    expect(onNewPerson).toHaveBeenCalledOnce();
    expect(onAssign).not.toHaveBeenCalled();
  });

  it("shows an auto badge only when identified automatically", () => {
    row({ label: "SPEAKER_00", displayName: "Alice", profileId: "p1", identifiedAuto: true });
    expect(screen.getByText("auto")).toBeTruthy();
  });

  it("hides the auto badge for a manually-named speaker", () => {
    row({ label: "SPEAKER_00", displayName: "Carol", profileId: null, identifiedAuto: false });
    expect(screen.queryByText("auto")).toBeNull();
  });
});
