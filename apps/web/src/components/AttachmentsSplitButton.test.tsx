import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { attachmentContentUrl: (rec: string, id: string) => `/api/recordings/${rec}/attachments/${id}/content?access_token=t` },
}));

import AttachmentsSplitButton from "./AttachmentsSplitButton";
import type { Attachment } from "../lib/types";

const file = (over: Partial<Attachment> = {}): Attachment => ({
  id: "a1", kind: "File", name: "notes.pdf", contentType: "application/pdf", sizeBytes: 100, url: null, ordinal: 0, ...over,
});
const link = (over: Partial<Attachment> = {}): Attachment => ({
  id: "u1", kind: "Url", name: "Spec", contentType: null, sizeBytes: 0, url: "https://x.test/spec", ordinal: 1, ...over,
});

describe("AttachmentsSplitButton", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("shows the attachment count and opens the manage modal on the main button", () => {
    const onManage = vi.fn();
    render(<AttachmentsSplitButton recordingId="rec1" attachments={[file(), link()]} onManage={onManage} />);
    fireEvent.click(screen.getByText("Attachments (2)"));
    expect(onManage).toHaveBeenCalledOnce();
  });

  it("disables the dropdown caret when there are no attachments", () => {
    render(<AttachmentsSplitButton recordingId="rec1" attachments={[]} onManage={vi.fn()} />);
    expect((screen.getByRole("button", { name: "Open an attachment" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText("Attachments (0)")).toBeTruthy();
  });

  it("opens a file attachment via the content URL and a URL attachment via its address", () => {
    const open = vi.fn();
    vi.stubGlobal("open", open);
    render(<AttachmentsSplitButton recordingId="rec1" attachments={[file(), link()]} onManage={vi.fn()} />);

    fireEvent.click(screen.getByRole("button", { name: "Open an attachment" }));
    fireEvent.click(screen.getByTitle("notes.pdf"));
    expect(open).toHaveBeenCalledWith("/api/recordings/rec1/attachments/a1/content?access_token=t", "_blank", "noopener");

    fireEvent.click(screen.getByRole("button", { name: "Open an attachment" }));
    fireEvent.click(screen.getByTitle("Spec"));
    expect(open).toHaveBeenCalledWith("https://x.test/spec", "_blank", "noopener");
  });
});
