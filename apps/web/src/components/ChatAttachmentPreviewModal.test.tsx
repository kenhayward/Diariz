import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import ChatAttachmentPreviewModal from "./ChatAttachmentPreviewModal";

describe("ChatAttachmentPreviewModal", () => {
  it("shows the attachment's name as the dialog title", () => {
    render(
      <ChatAttachmentPreviewModal name="Screenshot at 1:05" text="Row one" origin="ocr" onClose={() => {}} />,
    );

    expect(screen.getByRole("dialog").textContent).toContain("Screenshot at 1:05");
  });

  /// Extracted text is Markdown by construction - this app generated it - so a table read off a capture
  /// should read as a table here, which is the whole point of being able to look before you send.
  it("renders extracted text as Markdown", () => {
    const text = "| A | B |\n| --- | --- |\n| 1 | 2 |";
    render(<ChatAttachmentPreviewModal name="n" text={text} origin="ocr" onClose={() => {}} />);

    expect(screen.getByRole("dialog").querySelector("table")).toBeTruthy();
  });

  /// An uploaded document's extracted text is NOT Markdown - it is whatever was in the file. Running it
  /// through a Markdown renderer would eat underscores, asterisks and stray hashes out of a plain document.
  it("shows an uploaded file's text verbatim rather than as Markdown", () => {
    render(
      <ChatAttachmentPreviewModal name="notes.txt" text="a_b_c and # not a heading" origin="file" onClose={() => {}} />,
    );

    const dialog = screen.getByRole("dialog");
    expect(dialog.querySelector("h1")).toBeNull();
    expect(dialog.textContent).toContain("a_b_c and # not a heading");
  });

  /// The text is a model's transcription of arbitrary pixels, so it is untrusted whatever it looks like.
  ///
  /// The assertion is that no *executable* markup survives, not that no markup does: DOMPurify allows a
  /// plain `<img>` and strips the handler off it, which is the behaviour every other rendered surface in
  /// this app already relies on. By the time text reaches here it has also been through `ocrToMarkdown`,
  /// which escapes angle brackets - so this is the second line, not the first.
  it("strips executable markup from the text", () => {
    render(
      <ChatAttachmentPreviewModal
        name="n"
        text="<img src=x onerror=alert(1)>"
        origin="ocr"
        onClose={() => {}}
      />,
    );

    const img = screen.getByRole("dialog").querySelector("img");
    expect(img?.getAttribute("onerror")).toBeNull();
    expect(screen.getByRole("dialog").innerHTML).not.toContain("alert(1)");
  });

  it("does not render markup at all for an uploaded file, which is shown verbatim", () => {
    render(
      <ChatAttachmentPreviewModal name="n" text="<img src=x>" origin="file" onClose={() => {}} />,
    );

    expect(screen.getByRole("dialog").querySelector("img")).toBeNull();
    expect(screen.getByRole("dialog").textContent).toContain("<img src=x>");
  });

  it("closes on the close button", () => {
    const onClose = vi.fn();
    render(<ChatAttachmentPreviewModal name="n" text="t" origin="ocr" onClose={onClose} />);

    fireEvent.click(screen.getByRole("button", { name: /close/i }));

    expect(onClose).toHaveBeenCalled();
  });

  it("closes on Escape", () => {
    const onClose = vi.fn();
    render(<ChatAttachmentPreviewModal name="n" text="t" origin="ocr" onClose={onClose} />);

    fireEvent.keyDown(document, { key: "Escape" });

    expect(onClose).toHaveBeenCalled();
  });

  it("closes on a backdrop click but not on a click inside the panel", () => {
    const onClose = vi.fn();
    render(<ChatAttachmentPreviewModal name="n" text="t" origin="ocr" onClose={onClose} />);

    fireEvent.click(screen.getByRole("dialog"));
    expect(onClose).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByText("t"));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  /// A capture with a lot of text on it produces a lot of Markdown, and the dialog has to stay usable.
  it("scrolls its content rather than growing without limit", () => {
    render(<ChatAttachmentPreviewModal name="n" text={"line\n".repeat(500)} origin="ocr" onClose={() => {}} />);

    expect(screen.getByRole("dialog").querySelector(".overflow-y-auto")).toBeTruthy();
  });
});
