import { describe, it, expect } from "vitest";
import { hasSpeechRecognition } from "./dictationEngine";

describe("hasSpeechRecognition", () => {
  it("is true when the browser exposes SpeechRecognition and is not Electron", () => {
    const win = { webkitSpeechRecognition: function () {} } as unknown as Window;
    expect(hasSpeechRecognition(win)).toBe(true);
  });

  it("is false when neither SpeechRecognition constructor exists", () => {
    const win = {} as unknown as Window;
    expect(hasSpeechRecognition(win)).toBe(false);
  });

  it("is false in the Electron desktop shell even though the constructor exists (it fails there with no Google backend)", () => {
    const win = { webkitSpeechRecognition: function () {}, diariz: { isElectron: true } } as unknown as Window;
    expect(hasSpeechRecognition(win)).toBe(false);
  });
});
