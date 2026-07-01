import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import PickRecordingModal from "./PickRecordingModal";
import type { AttachmentDraft } from "../lib/types";

const draft: AttachmentDraft = {
  name: "Summary",
  content: "# Notes",
  recordings: [
    { id: "rec-1", title: "Standup" },
    { id: "rec-2", title: "Retro" },
  ],
};

describe("PickRecordingModal", () => {
  it("lists every candidate transcript", () => {
    render(<PickRecordingModal draft={draft} onCancel={() => {}} onPick={() => {}} />);
    expect(screen.getByText("Standup")).toBeTruthy();
    expect(screen.getByText("Retro")).toBeTruthy();
  });

  it("adds to the chosen transcript", () => {
    const onPick = vi.fn();
    render(<PickRecordingModal draft={draft} onCancel={() => {}} onPick={onPick} />);

    fireEvent.click(screen.getByRole("radio", { name: /retro/i }));
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    expect(onPick).toHaveBeenCalledWith("rec-2");
  });

  it("defaults to the first transcript when none is re-picked", () => {
    const onPick = vi.fn();
    render(<PickRecordingModal draft={draft} onCancel={() => {}} onPick={onPick} />);
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(onPick).toHaveBeenCalledWith("rec-1");
  });

  it("cancels without adding", () => {
    const onCancel = vi.fn();
    const onPick = vi.fn();
    render(<PickRecordingModal draft={draft} onCancel={onCancel} onPick={onPick} />);
    fireEvent.click(screen.getByRole("button", { name: /cancel/i }));
    expect(onCancel).toHaveBeenCalledOnce();
    expect(onPick).not.toHaveBeenCalled();
  });
});
