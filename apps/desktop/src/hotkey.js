"use strict";

const input = document.getElementById("combo");
const error = document.getElementById("error");
const save = document.getElementById("save");

// The combination captured so far, in Electron's own accelerator naming (e.g.
// "Control+Shift+9"). Seeded from the persisted value once load() resolves, then
// replaced by every keydown once main formats it (see below).
let pending = null;

window.hotkeyConfig.load().then((current) => {
  pending = current;
  input.value = current;
});

// This window is sandboxed (see main.js's showHotkeyWindow), so it can't require
// screenshotState.js directly to do the DOM-key-name-to-Electron-accelerator-name
// translation (e.g. the spacebar's e.key is a literal space, not "Space"; arrow keys
// report as "ArrowUp" etc rather than Electron's "Up"). Instead it sends the raw
// KeyboardEvent descriptor to main over IPC and displays back exactly what main would
// save - so the user sees the real accelerator, in Electron's naming, before saving it,
// and an unusable combination is flagged immediately rather than only as a save error.
input.addEventListener("keydown", async (e) => {
  e.preventDefault();
  const descriptor = {
    ctrlKey: e.ctrlKey,
    metaKey: e.metaKey,
    altKey: e.altKey,
    shiftKey: e.shiftKey,
    key: e.key,
  };
  const { accelerator, valid } = await window.hotkeyConfig.describe(descriptor);
  pending = accelerator;
  input.value = accelerator;
  error.textContent = valid ? "" : "Use at least one modifier (Ctrl, Alt, Shift) plus one key.";
});

async function submit() {
  error.textContent = "";
  save.disabled = true;
  try {
    const result = await window.hotkeyConfig.save(pending);
    if (result.ok) {
      window.close();
      return;
    }
    error.textContent = result.error || "Could not save that combination.";
  } finally {
    save.disabled = false;
  }
}

save.addEventListener("click", submit);
