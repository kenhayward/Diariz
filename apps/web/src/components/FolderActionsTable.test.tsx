import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { I18nextProvider } from "react-i18next";
import i18n from "../lib/i18n";
import FolderActionsTable from "./FolderActionsTable";
import type { ActionListItem } from "../lib/types";

const item: ActionListItem = {
  id: "a1", recordingId: "r1", recordingName: "Kickoff", text: "Ship it", actor: "Ana",
  deadline: "Fri", ordinal: 0, completed: false, completedAt: null, createdAt: "2026-07-01T00:00:00Z",
};

function renderTable(props: Partial<React.ComponentProps<typeof FolderActionsTable>> = {}) {
  return render(
    <I18nextProvider i18n={i18n}>
      <MemoryRouter>
        <FolderActionsTable
          items={[item]}
          onUpdate={vi.fn()}
          onToggleComplete={vi.fn()}
          onDelete={vi.fn()}
          {...props}
        />
      </MemoryRouter>
    </I18nextProvider>,
  );
}

describe("FolderActionsTable", () => {
  it("shows the read-only Meeting column and has no add control", () => {
    renderTable();
    expect(screen.getByText("Meeting")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Kickoff" }).getAttribute("href")).toBe("/recordings/r1");
    expect(screen.queryByRole("button", { name: /add action/i })).toBeNull();
  });

  it("commits an edit with the source recordingId", () => {
    const onUpdate = vi.fn();
    renderTable({ onUpdate });
    const cell = screen.getByLabelText("Action 1") as HTMLInputElement;
    fireEvent.change(cell, { target: { value: "Ship it now" } });
    fireEvent.blur(cell);
    expect(onUpdate).toHaveBeenCalledWith("r1", "a1", { text: "Ship it now" });
  });

  it("deletes with the source recordingId", () => {
    const onDelete = vi.fn();
    renderTable({ onDelete });
    fireEvent.click(screen.getByLabelText("Remove action 1"));
    expect(onDelete).toHaveBeenCalledWith("r1", "a1");
  });
});
