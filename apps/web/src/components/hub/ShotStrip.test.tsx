import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import ShotStrip from "./ShotStrip";
import type { ShotView } from "../../lib/types";

const shot = (over: Partial<ShotView> = {}): ShotView => ({
  id: "s1",
  capturedAtMs: 61_000,
  thumb: new Blob(["t"], { type: "image/jpeg" }),
  ...over,
});

describe("ShotStrip", () => {
  let urlCounter = 0;

  beforeEach(() => {
    urlCounter = 0;
    vi.spyOn(URL, "createObjectURL").mockImplementation(() => `blob:mock-${urlCounter++}`);
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("renders one thumbnail per capture, labelled with its stamp", () => {
    render(<ShotStrip shots={[shot(), shot({ id: "s2", capturedAtMs: 3_904_000 })]} onDelete={vi.fn()} />);
    expect(screen.getByAltText(/1:01/)).toBeTruthy();
    expect(screen.getByAltText(/1:05:04/)).toBeTruthy();
  });

  // The reason this component exists: deleting must name the capture, not its position.
  it("deletes by id, not by index", () => {
    const onDelete = vi.fn();
    render(<ShotStrip shots={[shot(), shot({ id: "s2" })]} onDelete={onDelete} />);
    fireEvent.click(screen.getAllByRole("button", { name: /delete/i })[1]);
    expect(onDelete).toHaveBeenCalledWith("s2");
  });

  it("revokes its object URLs when unmounted", () => {
    const { unmount } = render(<ShotStrip shots={[shot()]} onDelete={vi.fn()} />);
    unmount();
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:mock-0");
  });
});
