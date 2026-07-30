import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";

// The browser-tab icon and the desktop/n8n app icon are the same mark on purpose: a white microphone on
// an indigo rounded square. The mark lives in the n8n node's `diariz.svg`, which is also what
// `apps/desktop/build/make-app-icon.js` redraws to produce the Windows installer icon, the .icns, the app
// window and the tray. Nothing links the web copy to it, so this asserts they are byte-identical - the
// web favicon previously sat on an older teal logo for exactly that reason.
//
// If the brand mark changes: edit diariz.svg, copy it here, and re-run `node build/make-app-icon.js` in
// apps/desktop. All three then agree again.

const read = (relative: string) =>
  readFileSync(new URL(relative, import.meta.url), "utf8").replace(/\r\n/g, "\n").trim();

describe("the web favicon", () => {
  it("is the same mark as the desktop and n8n app icon", () => {
    expect(read("../../public/favicon.svg")).toBe(
      read("../../../../integrations/n8n-nodes-diariz/nodes/Diariz/diariz.svg"),
    );
  });
});
