import { describe, it, expect, vi } from "vitest";
import i18n from "../lib/i18n";
import { recordingMenu } from "./recordingMenu";

// Resolve labels through the real English catalog so the assertions below read naturally.
const t = i18n.getFixedT("en");

function build(overrides: Partial<Parameters<typeof recordingMenu>[0]> = {}) {
  const noop = vi.fn();
  return recordingMenu({
    onRename: noop, onCopyLink: noop, onRetranscribe: noop, onSummarise: noop, onMove: noop, onPlay: noop,
    onDownloadTranscript: noop, onDownloadAudio: noop, onDeleteAudio: noop, onDelete: noop,
    hasTranscript: true, hasAudio: true,
    ...overrides,
  }, t);
}

describe("recordingMenu", () => {
  it("lists the unified actions in order, without a Download both option", () => {
    const labels = build().map((a) => a.label);
    expect(labels).toEqual([
      "Rename",
      "Copy link",
      "Re-transcribe",
      "Summarise",
      "Move to section…",
      "Play",
      "Download transcript",
      "Download audio",
      "Delete audio",
      "Delete",
    ]);
    expect(labels).not.toContain("Download both");
    expect(labels).not.toContain("Download transcript (.srt)");
    expect(labels).not.toContain("Edit summary"); // detail-only (needs onEditSummary)
  });

  it("includes Edit summary only when an onEditSummary handler is provided (detail menu)", () => {
    expect(build().map((a) => a.label)).not.toContain("Edit summary");
    const labels = build({ onEditSummary: () => {} }).map((a) => a.label);
    expect(labels).toContain("Edit summary");
  });

  it("hides audio-dependent actions once the audio is deleted", () => {
    const labels = build({ hasAudio: false, onReidentify: () => {} }).map((a) => a.label);
    // Re-transcribe / Play / Download audio / Delete audio / Re-identify all need the audio.
    expect(labels).not.toContain("Re-transcribe");
    expect(labels).not.toContain("Play");
    expect(labels).not.toContain("Download audio");
    expect(labels).not.toContain("Delete audio");
    expect(labels).not.toContain("Re-identify speakers");
    // Transcript-only actions remain.
    expect(labels).toContain("Rename");
    expect(labels).toContain("Download transcript");
    expect(labels).toContain("Delete");
  });

  it("omits Play when no onPlay handler is provided (the list menu)", () => {
    const labels = build({ onPlay: undefined }).map((a) => a.label);
    expect(labels).not.toContain("Play");
  });

  it("includes Extract actions only when an onExtractActions handler is provided (detail menu)", () => {
    expect(build().map((a) => a.label)).not.toContain("Extract actions");
    const labels = build({ onExtractActions: () => {} }).map((a) => a.label);
    expect(labels).toContain("Extract actions");
  });

  it("disables transcript-dependent actions when there is no transcript", () => {
    const menu = build({ hasTranscript: false });
    const find = (label: string) => menu.find((a) => a.label === label)!;
    expect(find("Summarise").disabled).toBe(true);
    expect(find("Download transcript").disabled).toBe(true);
    expect(find("Download audio").disabled).toBeFalsy(); // audio is independent of the transcript
    expect(find("Rename").disabled).toBeFalsy();
  });

  it("disables Summarise while summarising", () => {
    const menu = build({ isSummarizing: true });
    expect(menu.find((a) => a.label === "Summarise")!.disabled).toBe(true);
  });
});
