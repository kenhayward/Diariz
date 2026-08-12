import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { UploadItem } from "../../lib/uploadQueue";
import UploadStatusList from "./UploadStatusList";

const item = (over: Partial<UploadItem>): UploadItem => ({
  id: "0-a.mp4",
  name: "Town Hall.mp4",
  status: "queued",
  ...over,
});

describe("UploadStatusList", () => {
  it("shows extraction progress as a percentage", () => {
    render(
      <UploadStatusList
        items={[item({ status: "extracting", progress: 0.42 })]}
        onClear={() => {}}
        onCancel={() => {}}
      />,
    );
    expect(screen.getByText(/42%/)).toBeTruthy();
  });

  it("cancels the item it belongs to", async () => {
    const onCancel = vi.fn();
    render(
      <UploadStatusList
        items={[item({ status: "extracting", progress: 0.1 })]}
        onClear={() => {}}
        onCancel={onCancel}
      />,
    );
    await userEvent.click(screen.getByRole("button", { name: /cancel/i }));
    expect(onCancel).toHaveBeenCalledWith("0-a.mp4");
  });

  it("offers no cancel once an item has settled", () => {
    render(
      <UploadStatusList items={[item({ status: "done" })]} onClear={() => {}} onCancel={() => {}} />,
    );
    expect(screen.queryByRole("button", { name: /cancel/i })).toBeNull();
  });

  it("treats a cancelled item as settled so the batch can be cleared", () => {
    render(
      <UploadStatusList
        items={[item({ status: "cancelled" })]}
        onClear={() => {}}
        onCancel={() => {}}
      />,
    );
    expect(screen.getByRole("button", { name: /clear/i })).toBeTruthy();
  });
});
