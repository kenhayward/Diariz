import { render, screen, fireEvent, act, waitFor } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import SpeakerAssign from "./SpeakerAssign";
import type { SpeakerProfile } from "../lib/types";

const profiles: SpeakerProfile[] = [
  { id: "p1", name: "Alice", sampleCount: 2 },
  { id: "p2", name: "Bob", sampleCount: 1 },
  { id: "p3", name: "Alicia", sampleCount: 0 },
];

function setup(overrides: Partial<React.ComponentProps<typeof SpeakerAssign>> = {}) {
  const onAssign = vi.fn();
  const onCreate = vi.fn();
  const onMulti = vi.fn();
  render(
    <SpeakerAssign
      label="SPEAKER_00"
      profiles={profiles}
      profileId={null}
      isMulti={false}
      onAssign={onAssign}
      onCreate={onCreate}
      onMulti={onMulti}
      {...overrides}
    />,
  );
  return { onAssign, onCreate, onMulti };
}

function openInput() {
  fireEvent.click(screen.getByRole("button", { name: /assign speaker_00/i }));
  return screen.getByRole("combobox");
}

describe("SpeakerAssign", () => {
  it("shows the current assignment on the trigger", () => {
    setup({ profileId: "p1" });
    expect(screen.getByRole("button", { name: /assign speaker_00/i }).textContent).toContain("Alice");
  });

  it("shows 'Multiple Speakers' on the trigger when multi", () => {
    setup({ isMulti: true });
    expect(screen.getByRole("button", { name: /assign speaker_00/i }).textContent).toContain("Multiple Speakers");
  });

  it("lists no people until the user types, then filters by contains", () => {
    setup();
    openInput();
    // Nothing typed yet → no person options (Alice/Bob/Alicia absent).
    expect(screen.queryByRole("option", { name: "Alice" })).toBeNull();
    expect(screen.queryByRole("option", { name: "Bob" })).toBeNull();

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "ali" } });
    // "ali" matches Alice and Alicia (case-insensitive contains), not Bob.
    expect(screen.getByRole("option", { name: "Alice" })).toBeTruthy();
    expect(screen.getByRole("option", { name: "Alicia" })).toBeTruthy();
    expect(screen.queryByRole("option", { name: "Bob" })).toBeNull();
  });

  it("assigns the chosen person and closes", () => {
    const { onAssign } = setup();
    openInput();
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Bob" } });
    fireEvent.click(screen.getByRole("option", { name: "Bob" }));
    expect(onAssign).toHaveBeenCalledWith("p2");
    expect(screen.queryByRole("combobox")).toBeNull(); // closed
  });

  it("offers Create for an unknown name and not for an exact match", () => {
    const { onCreate } = setup();
    openInput();

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Zara" } });
    const create = screen.getByRole("option", { name: /create "zara"/i });
    fireEvent.click(create);
    expect(onCreate).toHaveBeenCalledWith("Zara");

    // Re-open and type an exact existing name → no Create row.
    openInput();
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Alice" } });
    expect(screen.queryByRole("option", { name: /create "alice"/i })).toBeNull();
  });

  it("exposes Multiple speakers and Unassign actions", () => {
    const { onMulti, onAssign } = setup({ profileId: "p1" });
    openInput();
    fireEvent.click(screen.getByRole("option", { name: "Multiple Speakers" }));
    expect(onMulti).toHaveBeenCalledTimes(1);

    openInput();
    fireEvent.click(screen.getByRole("option", { name: "Unassigned" }));
    expect(onAssign).toHaveBeenCalledWith(null);
  });

  it("falls back to the given display name (not 'Unassigned') when no profile is assigned", () => {
    setup({ displayName: "SPEAKER_00" });
    expect(screen.getByRole("button", { name: /assign speaker_00/i }).textContent).toContain("SPEAKER_00");
  });

  it("shows a spinner while an async create is in flight, then closes", async () => {
    let finish!: () => void;
    const onCreate = vi.fn(() => new Promise<void>((r) => (finish = r)));
    setup({ onCreate });
    openInput();
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Zara" } });
    fireEvent.click(screen.getByRole("option", { name: /create "zara"/i }));

    // Still open, showing progress rather than snapping shut before the person exists.
    expect(await screen.findByRole("status")).toBeTruthy();
    expect(screen.queryByRole("option", { name: /create "zara"/i })).toBeNull();

    await act(async () => finish());
    await waitFor(() => expect(screen.queryByRole("status")).toBeNull());
    expect(screen.queryByRole("combobox")).toBeNull(); // closed once the create resolved
  });

  it("closes on Escape", () => {
    setup();
    openInput();
    expect(screen.getByRole("combobox")).toBeTruthy();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("combobox")).toBeNull();
  });
});
