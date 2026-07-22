import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import { I18nextProvider } from "react-i18next";
import i18n from "../lib/i18n";
import type { Attachment } from "../lib/types";

import FolderAttachmentsManager from "./FolderAttachmentsManager";

const fileAttachment: Attachment = {
  id: "a1", kind: "File", name: "spec.pdf", contentType: "application/pdf", sizeBytes: 100, url: null, ordinal: 0,
};
const markdownAttachment: Attachment = {
  id: "a2", kind: "File", name: "notes.md", contentType: "text/markdown", sizeBytes: 20, url: null, ordinal: 0,
};

// `canManage` is a plain prop resolved by the caller (SectionDetail, against the folder's real room) - no
// router/room mocking needed to exercise the gate here.
function renderManager(canManage: boolean, attachments: Attachment[] = [fileAttachment]) {
  return render(
    <I18nextProvider i18n={i18n}>
      <FolderAttachmentsManager sectionId="s1" attachments={attachments} canManage={canManage} onChange={() => {}} />
    </I18nextProvider>,
  );
}

describe("FolderAttachmentsManager permission gating", () => {
  it("shows add controls and Remove when canManage is true", () => {
    renderManager(true);

    expect(screen.getByText("Add file")).toBeTruthy();
    expect(screen.getByText("Add URL")).toBeTruthy();
    expect(screen.getByText("Remove")).toBeTruthy();
    // The name is editable (rename control) for a manager.
    expect(screen.getByDisplayValue("spec.pdf")).toBeTruthy();
  });

  it("hides add controls and Remove when canManage is false, but still allows Open", () => {
    renderManager(false);

    expect(screen.queryByText("Add file")).toBeNull();
    expect(screen.queryByText("Add URL")).toBeNull();
    expect(screen.queryByText("Remove")).toBeNull();
    // Read stays available: the name reads as plain text (not a rename input) and Open still shows.
    expect(screen.queryByDisplayValue("spec.pdf")).toBeNull();
    expect(screen.getByText("spec.pdf")).toBeTruthy();
    expect(screen.getByText("Open")).toBeTruthy();
  });

  it("offers the in-app Markdown editor when canManage is true but only a plain Open when it is false", () => {
    const { unmount } = renderManager(true, [markdownAttachment]);
    expect(screen.getByText("Edit")).toBeTruthy();
    unmount();

    renderManager(false, [markdownAttachment]);
    expect(screen.queryByText("Edit")).toBeNull();
    expect(screen.getByText("Open")).toBeTruthy();
  });
});
