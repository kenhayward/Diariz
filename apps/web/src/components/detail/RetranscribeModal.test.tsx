import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";

import RetranscribeModal from "./RetranscribeModal";

const LANGUAGES = [
  { code: "en", englishName: "English", nativeName: "English", rtl: false },
  { code: "de", englishName: "German", nativeName: "Deutsch", rtl: false },
  { code: "pt-BR", englishName: "Portuguese (Brazil)", nativeName: "Portugues (Brasil)", rtl: false },
];

function renderModal(props: Partial<Parameters<typeof RetranscribeModal>[0]> = {}) {
  const onConfirm = vi.fn();
  render(
    <RetranscribeModal
      initialMin={null}
      initialMax={null}
      initialLanguage={null}
      languages={LANGUAGES}
      hasRevisions={false}
      busy={false}
      onCancel={() => {}}
      onConfirm={onConfirm}
      {...props}
    />,
  );
  const confirm = () =>
    fireEvent.click(screen.getByRole("button", { name: /^re-transcribe$/i }));
  return { onConfirm, confirm };
}

const picker = () => screen.getByLabelText(/spoken language/i) as HTMLSelectElement;

describe("RetranscribeModal language", () => {
  // Whisper detects the language from the opening of the audio, so a recording that starts quiet can come
  // back as a language nobody spoke. Pinning it here is how a user fixes that recording.
  it("defaults to auto-detect when the recording is not pinned", () => {
    const { onConfirm, confirm } = renderModal();

    expect(picker().value).toBe("");

    confirm();
    expect(onConfirm).toHaveBeenCalledWith(null, null, null);
  });

  it("preselects the language the recording is already pinned to", () => {
    renderModal({ initialLanguage: "de" });

    expect(picker().value).toBe("de");
  });

  it("offers every supported language", () => {
    renderModal();

    const options = Array.from(picker().options).map((o) => o.value);
    expect(options).toEqual(["", "en", "de", "pt-BR"]);
  });

  it("passes the chosen language to the caller alongside the speaker hints", () => {
    const { onConfirm, confirm } = renderModal();

    fireEvent.change(picker(), { target: { value: "pt-BR" } });
    fireEvent.change(screen.getByLabelText(/minimum speakers/i), { target: { value: "2" } });
    confirm();

    expect(onConfirm).toHaveBeenCalledWith(2, null, "pt-BR");
  });

  /// Undoing a wrong pin has to be possible from the same dialog, or a recording pinned to the wrong
  /// language could never go back to being detected.
  it("returns to auto-detect when the user picks it back", () => {
    const { onConfirm, confirm } = renderModal({ initialLanguage: "de" });

    fireEvent.change(picker(), { target: { value: "" } });
    confirm();

    expect(onConfirm).toHaveBeenCalledWith(null, null, null);
  });
});
