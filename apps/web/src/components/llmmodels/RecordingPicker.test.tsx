import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { RecordingSummary } from "../../lib/types";

const { api } = vi.hoisted(() => ({ api: { listRecordings: vi.fn() } }));
vi.mock("../../lib/api", () => ({ api, apiErrorMessage: (e: unknown) => String(e) }));

import RecordingPicker from "./RecordingPicker";

function recording(over: Partial<RecordingSummary>): RecordingSummary {
  return {
    id: "r1",
    title: "2026-08-20 team sync",
    name: null,
    source: "Microphone",
    durationMs: 600000,
    status: "Transcribed",
    createdAt: "2026-08-20T09:00:00Z",
    sectionId: null,
    sectionName: null,
    hasActions: false,
    hasAudio: true,
    calendarEventId: null,
    ...over,
  } as RecordingSummary;
}

const NOTHING = { recordingId: null, title: null };

describe("RecordingPicker", () => {
  beforeEach(() => vi.clearAllMocks());

  it("offers only recordings that have a transcript", async () => {
    // A recording still uploading or transcribing has no segments, so testing against it can only error.
    api.listRecordings.mockResolvedValue([
      recording({ id: "done", name: "Finished meeting", status: "Transcribed" }),
      recording({ id: "pending", name: "Still uploading", status: "Uploaded" }),
      recording({ id: "running", name: "Being transcribed", status: "Transcribing" }),
    ]);

    render(<RecordingPicker value={NOTHING} onChange={vi.fn()} />);
    await userEvent.click(await screen.findByRole("button"));

    expect(await screen.findByText("Finished meeting")).toBeDefined();
    expect(screen.queryByText("Still uploading")).toBeNull();
    expect(screen.queryByText("Being transcribed")).toBeNull();
  });

  it("reports the chosen recording's id and display name", async () => {
    api.listRecordings.mockResolvedValue([recording({ id: "done", name: "Finished meeting" })]);
    const onChange = vi.fn();

    render(<RecordingPicker value={NOTHING} onChange={onChange} />);
    await userEvent.click(await screen.findByRole("button"));
    await userEvent.click(await screen.findByText("Finished meeting"));

    expect(onChange).toHaveBeenCalledWith("done", "Finished meeting");
  });

  it("falls back to the auto title when a recording has no name", async () => {
    // The app shows `name ?? title` everywhere; a picker that showed a blank row for an unnamed recording
    // would make most of the list unusable.
    api.listRecordings.mockResolvedValue([
      recording({ id: "done", name: null, title: "2026-08-20 team sync" }),
    ]);

    render(<RecordingPicker value={NOTHING} onChange={vi.fn()} />);
    await userEvent.click(await screen.findByRole("button"));

    expect(await screen.findByText("2026-08-20 team sync")).toBeDefined();
  });

  it("filters the list as you type", async () => {
    api.listRecordings.mockResolvedValue([
      recording({ id: "a", name: "Budget review" }),
      recording({ id: "b", name: "Hiring sync" }),
    ]);

    render(<RecordingPicker value={NOTHING} onChange={vi.fn()} />);
    await userEvent.click(await screen.findByRole("button"));
    await userEvent.type(await screen.findByRole("textbox"), "hir");

    expect(screen.queryByText("Budget review")).toBeNull();
    expect(screen.getByText("Hiring sync")).toBeDefined();
  });

  it("says so when there is nothing to test against", async () => {
    api.listRecordings.mockResolvedValue([recording({ id: "pending", status: "Uploaded" })]);

    render(<RecordingPicker value={NOTHING} onChange={vi.fn()} />);
    await userEvent.click(await screen.findByRole("button"));

    expect(await screen.findByText(/no transcribed recordings/i)).toBeDefined();
  });

  it("cannot be opened while disabled", async () => {
    // userEvent, not fireEvent: fireEvent fires the handler on a disabled control, so this would pass for a
    // reason the browser never reproduces.
    api.listRecordings.mockResolvedValue([recording({ id: "done", name: "Finished meeting" })]);

    render(<RecordingPicker value={NOTHING} onChange={vi.fn()} disabled />);
    await userEvent.click(screen.getByRole("button"));

    expect(screen.queryByText("Finished meeting")).toBeNull();
  });
});
