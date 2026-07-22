import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { I18nextProvider } from "react-i18next";
import i18n from "../lib/i18n";
import { RoomPermission } from "../lib/types";
import type { Attachment } from "../lib/types";

// The room the folder page is viewing; write controls are gated on ManageContents (mirrors the pattern
// RecordingsPanel already uses for folder create/rename/delete in a shared room).
const roomState = { permissions: 0 };
vi.mock("../lib/rooms", () => ({
  useRoom: () => ({ can: (perm: number) => (roomState.permissions & perm) !== 0 }),
}));

import FolderAttachmentsManager from "./FolderAttachmentsManager";

const fileAttachment: Attachment = {
  id: "a1", kind: "File", name: "spec.pdf", contentType: "application/pdf", sizeBytes: 100, url: null, ordinal: 0,
};
const markdownAttachment: Attachment = {
  id: "a2", kind: "File", name: "notes.md", contentType: "text/markdown", sizeBytes: 20, url: null, ordinal: 0,
};

function renderManager(attachments: Attachment[] = [fileAttachment]) {
  return render(
    <I18nextProvider i18n={i18n}>
      <FolderAttachmentsManager sectionId="s1" attachments={attachments} onChange={() => {}} />
    </I18nextProvider>,
  );
}

describe("FolderAttachmentsManager permission gating", () => {
  beforeEach(() => {
    roomState.permissions = 0;
  });

  it("shows add controls and Remove for a member with ManageContents", () => {
    roomState.permissions = RoomPermission.ManageContents;
    renderManager();

    expect(screen.getByText("Add file")).toBeTruthy();
    expect(screen.getByText("Add URL")).toBeTruthy();
    expect(screen.getByText("Remove")).toBeTruthy();
    // The name is editable (rename control) for a manager.
    expect(screen.getByDisplayValue("spec.pdf")).toBeTruthy();
  });

  it("hides add controls and Remove for a member without ManageContents, but still allows Open", () => {
    roomState.permissions = 0; // e.g. only CreateRecording, or nothing at all
    renderManager();

    expect(screen.queryByText("Add file")).toBeNull();
    expect(screen.queryByText("Add URL")).toBeNull();
    expect(screen.queryByText("Remove")).toBeNull();
    // Read stays available: the name reads as plain text (not a rename input) and Open still shows.
    expect(screen.queryByDisplayValue("spec.pdf")).toBeNull();
    expect(screen.getByText("spec.pdf")).toBeTruthy();
    expect(screen.getByText("Open")).toBeTruthy();
  });

  it("offers the in-app Markdown editor to a manager but only a plain Open to a non-manager", () => {
    roomState.permissions = RoomPermission.ManageContents;
    const { unmount } = renderManager([markdownAttachment]);
    expect(screen.getByText("Edit")).toBeTruthy();
    unmount();

    roomState.permissions = 0;
    renderManager([markdownAttachment]);
    expect(screen.queryByText("Edit")).toBeNull();
    expect(screen.getByText("Open")).toBeTruthy();
  });
});
