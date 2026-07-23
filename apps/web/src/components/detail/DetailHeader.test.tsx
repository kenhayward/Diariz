import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import DetailHeader from "./DetailHeader";

function renderHeader(overrides: Partial<Parameters<typeof DetailHeader>[0]> = {}) {
  const props = {
    title: "Standup",
    menu: [],
    hasAudio: true,
    hasTranscript: true,
    isPlaying: false,
    onPlay: vi.fn(),
    onStop: vi.fn(),
    onCopyLink: vi.fn(),
    onDownload: vi.fn(),
    onRename: vi.fn(),
    ...overrides,
  };
  render(<DetailHeader {...props} />);
  return props;
}

describe("DetailHeader rename control", () => {
  it("renders a rename button that calls onRename when clicked", () => {
    const { onRename } = renderHeader();

    fireEvent.click(screen.getByRole("button", { name: "Rename" }));

    expect(onRename).toHaveBeenCalledTimes(1);
  });

  it("places the rename button immediately after Play and before Copy link", () => {
    renderHeader();

    const buttons = screen.getAllByRole("button");
    const labels = buttons.map((b) => b.getAttribute("aria-label") ?? b.textContent);
    const playIdx = labels.findIndex((l) => l?.includes("Play"));
    const renameIdx = labels.indexOf("Rename");
    const copyIdx = labels.indexOf("Copy link");

    expect(renameIdx).toBe(playIdx + 1);
    expect(copyIdx).toBe(renameIdx + 1);
  });
});

describe("DetailHeader play/stop transport", () => {
  it("shows Play when isPlaying is false and calls onPlay when clicked", () => {
    const { onPlay, onStop } = renderHeader({ isPlaying: false });

    const btn = screen.getByRole("button", { name: "Play" });
    fireEvent.click(btn);

    expect(onPlay).toHaveBeenCalledTimes(1);
    expect(onStop).not.toHaveBeenCalled();
  });

  it("shows Stop when isPlaying is true and calls onStop when clicked", () => {
    const { onPlay, onStop } = renderHeader({ isPlaying: true });

    expect(screen.queryByRole("button", { name: "Play" })).toBeNull();
    const btn = screen.getByRole("button", { name: "Stop" });
    fireEvent.click(btn);

    expect(onStop).toHaveBeenCalledTimes(1);
    expect(onPlay).not.toHaveBeenCalled();
  });

  it("stays disabled when there is no audio, whether playing or not", () => {
    renderHeader({ isPlaying: false, hasAudio: false });
    expect(screen.getByRole("button", { name: "Play" }).hasAttribute("disabled")).toBe(true);
  });
});
