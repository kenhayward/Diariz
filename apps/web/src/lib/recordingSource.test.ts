import { describe, it, expect } from "vitest";
import type { TFunction } from "i18next";
import { sourceLabel } from "./recordingSource";
import type { RecordingSource } from "./types";

/// Echoes the key back, so a test asserts which key was asked for rather than what English happens to say.
const t = ((k: string) => k) as unknown as TFunction;

describe("sourceLabel", () => {
  it("names each capture source", () => {
    expect(sourceLabel("System", t)).toBe("workspace:sourceSystem");
    expect(sourceLabel("Combined", t)).toBe("workspace:sourceCombined");
    expect(sourceLabel("Upload", t)).toBe("workspace:sourceUpload");
    expect(sourceLabel("Microphone", t)).toBe("workspace:sourceMicrophone");
  });

  it("falls back to the microphone label for a source this build does not know", () => {
    // RecordingSource is append-only on the server (ints in Postgres), so an older web build can be handed
    // a source it has no case for. A label is better than an empty cell.
    expect(sourceLabel("Telepathy" as RecordingSource, t)).toBe("workspace:sourceMicrophone");
  });

  it("asks for keys in the workspace namespace, since callers use an un-namespaced t", () => {
    for (const s of ["System", "Combined", "Upload", "Microphone"] as RecordingSource[])
      expect(sourceLabel(s, t)).toMatch(/^workspace:/);
  });
});
