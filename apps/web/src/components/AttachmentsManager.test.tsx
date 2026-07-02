import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    addFileAttachment: vi.fn().mockResolvedValue(undefined),
    addUrlAttachment: vi.fn().mockResolvedValue(undefined),
    renameAttachment: vi.fn().mockResolvedValue(undefined),
    deleteAttachment: vi.fn().mockResolvedValue(undefined),
    attachmentContentUrl: (rec: string, id: string) => `/api/recordings/${rec}/attachments/${id}/content?access_token=t`,
  },
  apiErrorMessage: (e: unknown, fallback: string) => fallback ?? String(e),
}));

import { api } from "../lib/api";
import AttachmentsManager from "./AttachmentsManager";
import type { Attachment } from "../lib/types";

const file = (over: Partial<Attachment> = {}): Attachment => ({
  id: "a1", kind: "File", name: "notes.pdf", contentType: "application/pdf", sizeBytes: 100, url: null, ordinal: 0, ...over,
});

describe("AttachmentsManager", () => {
  beforeEach(() => vi.clearAllMocks());

  it("adds a URL attachment and fires onChange", async () => {
    const onChange = vi.fn();
    render(<AttachmentsManager recordingId="rec1" attachments={[]} onChange={onChange} />);
    fireEvent.change(screen.getByPlaceholderText(/https/i), { target: { value: "https://x.test/spec" } });
    fireEvent.change(screen.getByPlaceholderText(/link text/i), { target: { value: "Spec" } });
    fireEvent.click(screen.getByRole("button", { name: /add url/i }));
    await waitFor(() => expect(api.addUrlAttachment).toHaveBeenCalledWith("rec1", "https://x.test/spec", "Spec"));
    await waitFor(() => expect(onChange).toHaveBeenCalled());
  });

  it("renames an attachment on blur when changed", async () => {
    render(<AttachmentsManager recordingId="rec1" attachments={[file()]} onChange={vi.fn()} />);
    const name = screen.getByLabelText("Name") as HTMLInputElement;
    fireEvent.blur(name, { target: { value: "renamed.pdf" } });
    await waitFor(() => expect(api.renameAttachment).toHaveBeenCalledWith("rec1", "a1", "renamed.pdf"));
  });

  it("deletes an attachment after confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<AttachmentsManager recordingId="rec1" attachments={[file()]} onChange={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /remove/i }));
    await waitFor(() => expect(api.deleteAttachment).toHaveBeenCalledWith("rec1", "a1"));
  });

  it("opens a file attachment via its content URL", () => {
    const open = vi.fn();
    vi.stubGlobal("open", open);
    render(<AttachmentsManager recordingId="rec1" attachments={[file()]} onChange={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /^open$/i }));
    expect(open).toHaveBeenCalledWith("/api/recordings/rec1/attachments/a1/content?access_token=t", "_blank", "noopener");
  });

  it("shows an empty state when there are no attachments", () => {
    render(<AttachmentsManager recordingId="rec1" attachments={[]} onChange={vi.fn()} />);
    expect(screen.getByText(/no attachments/i)).toBeTruthy();
  });
});
