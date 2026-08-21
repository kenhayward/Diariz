import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import ChatScreenshotTray from "./ChatScreenshotTray";
import type { ChatScreenshotRef } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: { screenshotThumbUrl: (r: string, s: string) => `/thumb/${r}/${s}` },
}));

const shots: ChatScreenshotRef[] = [
  { recordingId: "r1", screenshotId: "a" },
  { recordingId: "r1", screenshotId: "b" },
];

describe("ChatScreenshotTray", () => {
  it("renders nothing when nothing is attached", () => {
    const { container } = render(<ChatScreenshotTray shots={[]} onRemove={() => {}} />);

    expect(container.firstChild).toBeNull();
  });

  it("renders one thumbnail per attached capture", () => {
    render(<ChatScreenshotTray shots={shots} onRemove={() => {}} />);

    const images = screen.getAllByRole("img");
    expect(images).toHaveLength(2);
    expect(images[0].getAttribute("src")).toBe("/thumb/r1/a");
    expect(images[1].getAttribute("src")).toBe("/thumb/r1/b");
  });

  it("removes the capture whose control was clicked, not the first", async () => {
    const onRemove = vi.fn();
    render(<ChatScreenshotTray shots={shots} onRemove={onRemove} />);

    await userEvent.click(screen.getAllByRole("button", { name: /remove screenshot/i })[1]);

    expect(onRemove).toHaveBeenCalledTimes(1);
    expect(onRemove).toHaveBeenCalledWith({ recordingId: "r1", screenshotId: "b" });
  });

  it("gives every capture its own remove control", () => {
    render(<ChatScreenshotTray shots={shots} onRemove={() => {}} />);

    expect(screen.getAllByRole("button", { name: /remove screenshot/i })).toHaveLength(2);
  });

  /// Two captures from different recordings can share nothing but their position; keying on the
  /// screenshot id alone would collide if ids were ever reused across recordings.
  it("keeps captures from different recordings distinct", () => {
    render(
      <ChatScreenshotTray
        shots={[
          { recordingId: "r1", screenshotId: "a" },
          { recordingId: "r2", screenshotId: "a" },
        ]}
        onRemove={() => {}}
      />,
    );

    const images = screen.getAllByRole("img");
    expect(images[0].getAttribute("src")).toBe("/thumb/r1/a");
    expect(images[1].getAttribute("src")).toBe("/thumb/r2/a");
  });
});
