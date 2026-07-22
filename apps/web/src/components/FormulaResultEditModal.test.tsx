import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";

// The TipTap editor is a trusted third-party; we don't exercise ProseMirror in jsdom (mirrors
// MeetingMinutesEditModal.test.tsx's mock).
vi.mock("@tiptap/react", () => ({
  useEditor: () => ({
    storage: { markdown: { getMarkdown: () => "# Edited" } },
    chain: () => ({ focus: () => ({ toggleBold: () => ({ run: () => {} }) }) }),
  }),
  EditorContent: () => <div data-testid="editor" />,
}));

import FormulaResultEditModal from "./FormulaResultEditModal";

describe("FormulaResultEditModal editable gating", () => {
  it("shows the editor with Save when editable (the default)", async () => {
    render(
      <FormulaResultEditModal
        name="Risk Register"
        load={() => Promise.resolve("# Body")}
        save={() => Promise.resolve()}
        onClose={() => {}}
      />,
    );

    expect(await screen.findByTestId("editor")).toBeTruthy();
    expect(screen.getByRole("button", { name: /^save$/i })).toBeTruthy();
  });

  it("renders read-only Markdown with no Save when editable is false", async () => {
    render(
      <FormulaResultEditModal
        name="Risk Register"
        load={() => Promise.resolve("# Body")}
        save={() => Promise.resolve()}
        onClose={() => {}}
        editable={false}
      />,
    );

    await waitFor(() => expect(screen.getByRole("dialog")).toBeTruthy());
    expect(screen.queryByTestId("editor")).toBeNull();
    expect(screen.queryByRole("button", { name: /^save$/i })).toBeNull();
    expect(screen.getByRole("button", { name: /^close$/i })).toBeTruthy();
  });
});
