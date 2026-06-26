import { describe, it, expect, vi } from "vitest";
import { recordingMenu } from "./recordingMenu";

function build(overrides: Partial<Parameters<typeof recordingMenu>[0]> = {}) {
  const noop = vi.fn();
  return recordingMenu({
    onRename: noop, onRetranscribe: noop, onSummarise: noop, onMove: noop, onPlay: noop,
    onDownloadTxt: noop, onDownloadAudio: noop, onDelete: noop,
    hasTranscript: true,
    ...overrides,
  });
}

describe("recordingMenu", () => {
  it("lists the unified actions in order, without a Download both option", () => {
    const labels = build().map((a) => a.label);
    expect(labels).toEqual([
      "Rename",
      "Re-transcribe",
      "Summarise",
      "Move to section…",
      "Play",
      "Download transcript",
      "Download audio",
      "Delete",
    ]);
    expect(labels).not.toContain("Download both");
    expect(labels).not.toContain("Download transcript (.srt)");
  });

  it("omits Play when no onPlay handler is provided (the list menu)", () => {
    const labels = build({ onPlay: undefined }).map((a) => a.label);
    expect(labels).not.toContain("Play");
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
