import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";

import RegenerateAiModal from "./RegenerateAiModal";

function renderModal(props: Partial<Parameters<typeof RegenerateAiModal>[0]> = {}) {
  const onConfirm = vi.fn().mockResolvedValue(undefined);
  const onCancel = vi.fn();
  render(
    <RegenerateAiModal
      busy={false}
      summaryEdited={false}
      minutesEdited={false}
      onCancel={onCancel}
      onConfirm={onConfirm}
      {...props}
    />,
  );
  return { onConfirm, onCancel };
}

// The modal shipped with every string hardcoded in Italian; these pin that it reads the i18n catalogue.
describe("RegenerateAiModal text", () => {
  it("titles the dialog and explains that the audio is not transcribed again", () => {
    renderModal();

    expect(screen.getByRole("dialog", { name: "Regenerate AI processing" })).toBeTruthy();
    expect(
      screen.getByText("Choose what to run on the existing transcript. The audio will not be transcribed again."),
    ).toBeTruthy();
  });

  it("names every operation with its description", () => {
    renderModal();

    for (const [label, description] of [
      ["Summary", "Regenerate the transcript summary"],
      ["Meeting minutes", "Regenerate the meeting minutes"],
      ["Action items", "Extract the action items from the transcript again"],
      ["Tags", "Refresh the AI-suggested tags"],
      ["Embeddings", "Rebuild the semantic search index for the transcript"],
    ]) {
      expect(screen.getByText(label)).toBeTruthy();
      expect(screen.getByText(description)).toBeTruthy();
    }
  });

  it("offers Cancel and Start processing", () => {
    const { onCancel } = renderModal();

    expect(screen.getByRole("button", { name: "Start processing" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onCancel).toHaveBeenCalled();
  });

  it("shows Queueing... on the confirm button while busy", () => {
    renderModal({ busy: true });

    expect(screen.getByRole("button", { name: "Queueing..." })).toBeTruthy();
  });

  it("asks for consent before replacing a hand-edited summary", () => {
    renderModal({ summaryEdited: true });

    fireEvent.click(screen.getByRole("checkbox", { name: /^Summary/ }));

    expect(
      screen.getByText(
        "The selected summary or meeting minutes contain manual edits. I agree to replace them.",
      ),
    ).toBeTruthy();
  });
});
