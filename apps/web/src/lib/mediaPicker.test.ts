import { afterEach, describe, expect, it, vi } from "vitest";
import { MEDIA_PICKER_TYPES, pickMediaFiles } from "./mediaPicker";

/**
 * `<input accept>` gives no control over the label the browser puts on its filter dropdown - Chromium
 * calls it "Custom Files". Only `showOpenFilePicker` can name it, via `types[].description`, and MDN
 * marks that API "not Baseline": Chromium has it, Firefox and Safari do not. So this is a progressive
 * enhancement, and the fallback path has to stay exactly as good as it is today.
 */
const w = globalThis as unknown as { showOpenFilePicker?: unknown };

afterEach(() => {
  delete w.showOpenFilePicker;
  vi.restoreAllMocks();
});

describe("MEDIA_PICKER_TYPES", () => {
  it("names the filter, which is the whole point of using this API", () => {
    expect(MEDIA_PICKER_TYPES[0].description).toBe("Audio and video files");
  });

  it("offers audio and video extensions under their MIME groups", () => {
    const accept = MEDIA_PICKER_TYPES[0].accept;
    expect(accept["audio/*"]).toContain(".wav");
    expect(accept["audio/*"]).toContain(".m4b");
    expect(accept["video/*"]).toContain(".mp4");
    expect(accept["video/*"]).toContain(".mkv");
  });
});

describe("pickMediaFiles", () => {
  it("returns null when the API is missing, so the caller falls back to the input", () => {
    expect(pickMediaFiles).toBeTypeOf("function");
    return expect(pickMediaFiles()).resolves.toBeNull();
  });

  it("returns the chosen files when the API is present", async () => {
    const file = new File(["x"], "talk.mp4");
    w.showOpenFilePicker = vi.fn().mockResolvedValue([{ getFile: () => Promise.resolve(file) }]);

    await expect(pickMediaFiles()).resolves.toEqual([file]);
  });

  it("passes the named filter and allows multiple selection", async () => {
    const picker = vi.fn().mockResolvedValue([]);
    w.showOpenFilePicker = picker;

    await pickMediaFiles();

    expect(picker).toHaveBeenCalledWith(
      expect.objectContaining({ multiple: true, types: MEDIA_PICKER_TYPES }),
    );
  });

  it("treats a cancelled dialog as 'no files', not an error", async () => {
    // Chromium throws AbortError when the user dismisses the picker. Surfacing that as a failure would
    // put an error toast in front of someone who simply changed their mind.
    const abort = Object.assign(new Error("The user aborted a request."), { name: "AbortError" });
    w.showOpenFilePicker = vi.fn().mockRejectedValue(abort);

    await expect(pickMediaFiles()).resolves.toEqual([]);
  });

  it("falls back to the input if the picker fails for any other reason", async () => {
    // e.g. a non-secure context, or a browser that exposes the name but refuses the call. Returning
    // null means the caller opens the ordinary dialog rather than the upload silently doing nothing.
    w.showOpenFilePicker = vi.fn().mockRejectedValue(new Error("not allowed"));

    await expect(pickMediaFiles()).resolves.toBeNull();
  });
});
