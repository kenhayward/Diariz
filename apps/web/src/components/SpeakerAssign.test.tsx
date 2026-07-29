import { render, screen, fireEvent, act, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import SpeakerAssign from "./SpeakerAssign";
import { api } from "../lib/api";
import type { Person } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: { searchPeople: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function person(id: string, name: string, extra: Partial<Person> = {}): Person {
  return {
    id, name, title: null, companyName: null, email: null, phone: null,
    isInternal: false, voiceprintOptOut: false, hasVoiceprint: false, sampleCount: 0,
    linkedUserId: null, isSelf: false, canManageBiometrics: false,
    createdAt: "2026-07-29T00:00:00Z", updatedAt: "2026-07-29T00:00:00Z", ...extra,
  };
}

const people = [person("p1", "Alice"), person("p2", "Bob"), person("p3", "Alicia")];

beforeEach(() => {
  vi.clearAllMocks();
  // The server does the matching now, so the fake does too - the component no longer filters.
  mock(api.searchPeople).mockImplementation(async (q: string) =>
    people.filter((p) => p.name.toLowerCase().includes(q.toLowerCase())));
});

function setup(overrides: Partial<React.ComponentProps<typeof SpeakerAssign>> = {}) {
  const onAssign = vi.fn();
  const onCreate = vi.fn();
  const onMulti = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <SpeakerAssign
        label="SPEAKER_00"
        isMulti={false}
        onAssign={onAssign}
        onCreate={onCreate}
        onMulti={onMulti}
        {...overrides}
      />
    </QueryClientProvider>,
  );
  return { onAssign, onCreate, onMulti };
}

function openInput() {
  fireEvent.click(screen.getByRole("button", { name: /assign speaker_00/i }));
  return screen.getByRole("combobox");
}

describe("SpeakerAssign", () => {
  it("shows the current assignment on the trigger", () => {
    setup({ displayName: "Alice" });
    expect(screen.getByRole("button", { name: /assign speaker_00/i }).textContent).toContain("Alice");
  });

  it("shows 'Multiple Speakers' on the trigger when multi", () => {
    setup({ isMulti: true });
    expect(screen.getByRole("button", { name: /assign speaker_00/i }).textContent).toContain("Multiple Speakers");
  });

  it("queries nothing until two characters are typed, then shows what the server returned", async () => {
    setup();
    openInput();
    expect(screen.queryByRole("option", { name: "Alice" })).toBeNull();
    expect(api.searchPeople).not.toHaveBeenCalled();

    // One character must not query - otherwise the first keystroke pulls most of the directory.
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "a" } });
    expect(api.searchPeople).not.toHaveBeenCalled();

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "ali" } });
    expect(await screen.findByRole("option", { name: "Alice" })).toBeTruthy();
    expect(screen.getByRole("option", { name: "Alicia" })).toBeTruthy();
    expect(screen.queryByRole("option", { name: "Bob" })).toBeNull();
    expect(api.searchPeople).toHaveBeenCalledWith("ali");
  });

  it("assigns the chosen person and closes", async () => {
    const { onAssign } = setup();
    openInput();
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Bob" } });
    // Re-query rather than clicking the awaited node: the results arriving re-renders the list, which
    // detaches the element findBy returned.
    await screen.findByRole("option", { name: "Bob" });
    fireEvent.click(screen.getByRole("option", { name: "Bob" }));
    await waitFor(() => expect(onAssign).toHaveBeenCalledWith("p2"));
    expect(screen.queryByRole("combobox")).toBeNull(); // closed
  });

  it("offers Create for an unknown name and not for an exact match", async () => {
    const { onCreate } = setup();
    openInput();

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Zara" } });
    fireEvent.click(await screen.findByRole("option", { name: /create "zara"/i }));
    expect(onCreate).toHaveBeenCalledWith("Zara");

    // Re-open and type an exact existing name → no Create row.
    openInput();
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Alice" } });
    await screen.findByRole("option", { name: "Alice" });
    expect(screen.queryByRole("option", { name: /create "alice"/i })).toBeNull();
  });

  it("exposes Multiple speakers and Unassign actions", () => {
    const { onMulti, onAssign } = setup({ displayName: "Alice" });
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
