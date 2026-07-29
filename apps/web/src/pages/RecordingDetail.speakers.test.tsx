import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, expect, it, vi, beforeEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { SpeakerRow } from "./RecordingDetail";
import { api } from "../lib/api";
import type { SpeakerInfo, Person } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: { searchPeople: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function person(id: string, name: string): Person {
  return {
    id, name, title: null, companyName: null, email: null, phone: null,
    isInternal: false, voiceprintOptOut: false, hasVoiceprint: true, sampleCount: 1,
    linkedUserId: null, isSelf: false, canManageBiometrics: false,
    createdAt: "2026-07-29T00:00:00Z", updatedAt: "2026-07-29T00:00:00Z",
  };
}

const people = [person("p1", "Alice"), person("p2", "Bob")];

beforeEach(() => {
  vi.clearAllMocks();
  mock(api.searchPeople).mockImplementation(async (q: string) =>
    people.filter((p) => p.name.toLowerCase().includes(q.toLowerCase())));
});

function row(
  info: SpeakerInfo | undefined,
  handlers: Partial<{
    onAssign: (id: string | null) => void;
    onCreate: (name: string) => void;
    onMulti: () => void;
    onTogglePlay: () => void;
    onDelete: (name: string) => void;
    onSelect: () => void;
    selected: boolean;
    count: number;
    durationMs: number;
    canPlay: boolean;
    playing: boolean;
  }> = {},
) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SpeakerRow
      label="SPEAKER_00"
      info={info}
      initial={info?.displayName ?? "SPEAKER_00"}
      count={handlers.count ?? 3}
      durationMs={handlers.durationMs ?? 0}
      canPlay={handlers.canPlay ?? true}
      playing={handlers.playing ?? false}
      selected={handlers.selected ?? false}
      onSelect={handlers.onSelect ?? (() => {})}
      onTogglePlay={handlers.onTogglePlay ?? (() => {})}
      onDelete={handlers.onDelete ?? (() => {})}
      onAssign={handlers.onAssign ?? (() => {})}
      onCreate={handlers.onCreate ?? (() => {})}
      onMulti={handlers.onMulti ?? (() => {})}
    />
    </QueryClientProvider>,
  );
}

/// The whole row is a button (aria-label "Show <name>'s segments") that toggles the speaker's segment table.
function speakerRowButton() {
  return screen.getByRole("button", { name: /show .*'s segments/i });
}

const speaker = (over: Partial<SpeakerInfo> = {}): SpeakerInfo => ({
  label: "SPEAKER_00", displayName: "SPEAKER_00", personId: null,
  identifiedAuto: false, isMultiSpeaker: false,
  title: null, companyName: null, email: null, phone: null, isInternal: null, ...over,
});

function openAssign() {
  fireEvent.click(screen.getByRole("button", { name: "Assign SPEAKER_00 to a person" }));
  return screen.getByRole("combobox");
}

describe("SpeakerRow", () => {
  it("assigning via the typeahead picks the chosen person", async () => {
    const onAssign = vi.fn();
    row(speaker(), { onAssign });

    openAssign();
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Bob" } });
    // Results come from the server now, so the option appears a tick later.
    await screen.findByRole("option", { name: "Bob" });
    fireEvent.click(screen.getByRole("option", { name: "Bob" }));

    expect(onAssign).toHaveBeenCalledWith("p2");
  });

  it("the Unassign action passes null", () => {
    const onAssign = vi.fn();
    row(speaker({ displayName: "Alice", personId: "p1", identifiedAuto: true }), { onAssign });

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
    row(speaker({ displayName: "Alice", personId: "p1", identifiedAuto: true }));
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

  it("shows the speaker's segment count and total duration", () => {
    row(speaker({ displayName: "Alice" }), { count: 4, durationMs: 65_000 });
    // Rendered together as "4 segments · 1:05".
    expect(screen.getByText(/4 segments · 1:05/)).toBeTruthy();
  });

  it("Delete confirms then calls onDelete with the current name", () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const onDelete = vi.fn();
    row(speaker({ displayName: "Alice" }), { onDelete });
    fireEvent.click(screen.getByRole("button", { name: /delete alice's segments/i }));
    expect(onDelete).toHaveBeenCalledWith("Alice");
  });

  it("clicking the row toggles selection of the speaker", () => {
    const onSelect = vi.fn();
    row(speaker({ displayName: "Alice" }), { onSelect });
    fireEvent.click(speakerRowButton());
    expect(onSelect).toHaveBeenCalledOnce();
  });

  it("marks the row as pressed when selected", () => {
    row(speaker({ displayName: "Alice" }), { selected: true });
    expect(speakerRowButton().getAttribute("aria-pressed")).toBe("true");
  });

  it("clicking the assign typeahead does not toggle selection", () => {
    const onSelect = vi.fn();
    row(speaker({ displayName: "Alice" }), { onSelect });
    openAssign();
    expect(onSelect).not.toHaveBeenCalled();
  });

  it("clicking play does not toggle selection", () => {
    const onSelect = vi.fn();
    const onTogglePlay = vi.fn();
    row(speaker({ displayName: "Alice" }), { onSelect, onTogglePlay });
    fireEvent.click(screen.getByRole("button", { name: /play alice's segments/i }));
    expect(onTogglePlay).toHaveBeenCalledOnce();
    expect(onSelect).not.toHaveBeenCalled();
  });

  it("clicking delete does not toggle selection", () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const onSelect = vi.fn();
    const onDelete = vi.fn();
    row(speaker({ displayName: "Alice" }), { onSelect, onDelete });
    fireEvent.click(screen.getByRole("button", { name: /delete alice's segments/i }));
    expect(onDelete).toHaveBeenCalledWith("Alice");
    expect(onSelect).not.toHaveBeenCalled();
  });

  // ---- Person details on an identified speaker ----

  it("shows the identified person's title and company", () => {
    row(speaker({
      displayName: "Ada Lovelace", personId: "p1", identifiedAuto: true,
      title: "Engineer", companyName: "Analytical Engines", isInternal: true,
    }));

    expect(screen.getByText(/Engineer at Analytical Engines/i)).toBeTruthy();
    expect(screen.getByText("Internal")).toBeTruthy();
  });

  it("marks an external person as external", () => {
    row(speaker({ displayName: "Grace Hopper", personId: "p2", title: "Admiral", isInternal: false }));

    expect(screen.getByText("External")).toBeTruthy();
  });

  /// Nothing is known about an anonymous speaker, so nothing is claimed about them.
  it("shows no person details for an anonymous speaker", () => {
    row(speaker());

    expect(screen.queryByText("Internal")).toBeNull();
    expect(screen.queryByText("External")).toBeNull();
  });

  /// Overlapping voices are not one person, so the server sends no details and the row must not invent any.
  it("shows no person details for a Multiple Speakers slot", () => {
    row(speaker({ displayName: "Multiple Speakers", isMultiSpeaker: true }));

    expect(screen.queryByText("Internal")).toBeNull();
    expect(screen.queryByText("External")).toBeNull();
  });
});
