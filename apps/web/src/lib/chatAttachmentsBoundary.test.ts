import { describe, it, expect } from "vitest";

/// `chatAttachments` is an in-TAB pub/sub - a module-level subscriber set, deliberately not a
/// BroadcastChannel. That is correct for the main window, where the publisher (a screenshot viewer, the
/// live notes popover) and the subscriber (`ChatPanel`, `Workspace`) are siblings in one document.
///
/// It is silently wrong in the pop-out notes window. That window is a second `BrowserWindow` with its
/// own module registry and no chat panel in it, so `attachScreenshotToChat` called there publishes to an
/// empty set: no error, no warning, nothing on screen, and a button that looks like it worked. The
/// pop-out must relay over `notesChannel` and let the host do it.
///
/// Nothing else would catch the mistake. Importing the module there compiles, type-checks, renders and
/// passes every other test in this repo, so the module graph is asserted directly - the same reasoning
/// as `releaseNotes/bundleBoundary.test.ts`.
const sources = import.meta.glob("../**/*.{ts,tsx}", {
  eager: true,
  query: "?raw",
  import: "default",
}) as Record<string, string>;

/// Vite keys a glob relative to the file that declares it, so these are paths from `src/lib/`, not from
/// `src/`. Getting that wrong is silent - a lookup returns `undefined`, and a `not.toMatch` against
/// `undefined` passes - so their existence is asserted first.
const POPOUT_PAGE = "../pages/NotesPopout.tsx";
const CHANNEL = "./notesChannel.ts";
const MODULE = "./chatAttachments.ts";

describe("the pop-out notes window never attaches to chat directly", () => {
  it("scans the files it claims to check", () => {
    for (const key of [POPOUT_PAGE, CHANNEL, MODULE]) {
      expect(sources[key], `${key} was not picked up by the source scan`).toBeDefined();
    }
  });

  it("does not import chatAttachments", () => {
    expect(sources[POPOUT_PAGE]).not.toMatch(/from\s+"[^"]*chatAttachments"/);
  });

  it("relays over the notes channel instead", () => {
    // The positive half. Without it, deleting both the import and the relay would leave the test above
    // passing over a window whose chat buttons do nothing at all.
    expect(sources[POPOUT_PAGE]).toMatch(/transcriptToChat/);
    expect(sources[POPOUT_PAGE]).toMatch(/shotToChat/);
    expect(sources[CHANNEL]).toMatch(/transcriptToChat/);
  });
});
