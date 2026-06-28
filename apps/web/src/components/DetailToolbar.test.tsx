import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import DetailToolbar from "./DetailToolbar";

function build(overrides: Partial<Parameters<typeof DetailToolbar>[0]> = {}) {
  const props = {
    onRename: vi.fn(),
    onRetranscribe: vi.fn(),
    onMove: vi.fn(),
    onExtractActions: vi.fn(),
    onEmailTranscript: vi.fn(),
    onDownloadTranscript: vi.fn(),
    hasTranscript: true,
    ...overrides,
  };
  render(<DetailToolbar {...props} />);
  return props;
}

const NAMES = [
  "Rename",
  "Re-transcribe",
  "Move to section",
  "Extract actions",
  "Email me the transcript",
  "Download transcript",
];

describe("DetailToolbar", () => {
  it("renders one icon button per action, each with hover text (title) and an accessible name", () => {
    build();
    for (const name of NAMES) {
      const btn = screen.getByRole("button", { name });
      expect(btn).toBeTruthy();
      expect(btn.getAttribute("title")).toBe(name); // hover tooltip
      expect(btn.querySelector("svg")).toBeTruthy(); // graphical button
    }
  });

  it("invokes the matching handler when a button is clicked", () => {
    const p = build();
    fireEvent.click(screen.getByRole("button", { name: "Rename" }));
    fireEvent.click(screen.getByRole("button", { name: "Re-transcribe" }));
    fireEvent.click(screen.getByRole("button", { name: "Move to section" }));
    fireEvent.click(screen.getByRole("button", { name: "Extract actions" }));
    fireEvent.click(screen.getByRole("button", { name: "Email me the transcript" }));
    fireEvent.click(screen.getByRole("button", { name: "Download transcript" }));
    expect(p.onRename).toHaveBeenCalledTimes(1);
    expect(p.onRetranscribe).toHaveBeenCalledTimes(1);
    expect(p.onMove).toHaveBeenCalledTimes(1);
    expect(p.onExtractActions).toHaveBeenCalledTimes(1);
    expect(p.onEmailTranscript).toHaveBeenCalledTimes(1);
    expect(p.onDownloadTranscript).toHaveBeenCalledTimes(1);
  });

  it("disables the transcript-dependent buttons when there is no transcript", () => {
    build({ hasTranscript: false });
    expect((screen.getByRole("button", { name: "Extract actions" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Email me the transcript" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Download transcript" }) as HTMLButtonElement).disabled).toBe(true);
    // Rename / Re-transcribe / Move don't depend on a transcript.
    expect((screen.getByRole("button", { name: "Rename" }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: "Re-transcribe" }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: "Move to section" }) as HTMLButtonElement).disabled).toBe(false);
  });
});
