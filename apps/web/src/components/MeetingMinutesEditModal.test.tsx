import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";

// The TipTap editor is a trusted third-party; we don't exercise ProseMirror in jsdom. Mock useEditor to
// return a stub whose markdown storage yields a fixed string, so we can test the modal's save/cancel wiring.
vi.mock("@tiptap/react", () => ({
  useEditor: () => ({
    storage: { markdown: { getMarkdown: () => "# Edited minutes\n\n- point" } },
    chain: () => ({ focus: () => ({ toggleBold: () => ({ run: () => {} }) }) }),
  }),
  EditorContent: () => <div data-testid="editor" />,
}));

import MeetingMinutesEditModal from "./MeetingMinutesEditModal";

describe("MeetingMinutesEditModal", () => {
  it("Save serialises the editor to Markdown and calls onSave", async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<MeetingMinutesEditModal initial="# Old" onClose={() => {}} onSave={onSave} />);

    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(onSave).toHaveBeenCalledWith("# Edited minutes\n\n- point"));
  });

  it("Cancel closes without saving", () => {
    const onClose = vi.fn();
    const onSave = vi.fn();
    render(<MeetingMinutesEditModal initial="# Old" onClose={onClose} onSave={onSave} />);

    fireEvent.click(screen.getByRole("button", { name: /^cancel$/i }));

    expect(onClose).toHaveBeenCalled();
    expect(onSave).not.toHaveBeenCalled();
  });
});
