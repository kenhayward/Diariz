"use strict";

const input = document.getElementById("combo");
const error = document.getElementById("error");
const save = document.getElementById("save");

// The combination captured so far, in Electron's own accelerator naming (e.g.
// "Control+Shift+9"). Seeded from the persisted value once load() resolves, then
// replaced by every keydown once main formats it (see below).
let pending = null;

// Inert until the persisted hotkey has actually loaded: a click before then would send
// `save(null)` and show the generic "needs a modifier" error instead of the real state.
save.disabled = true;

window.hotkeyConfig.load().then((current) => {
  pending = current;
  input.value = current;
  save.disabled = false;
});

// This window is sandboxed (see main.js's showHotkeyWindow), so it can't require
// screenshotState.js directly to do the DOM-key-to-Electron-accelerator translation.
// It sends `code` (the physical key, e.g. "Digit3", "KeyA", "Space", "ArrowUp"), not
// `key` (the possibly shift-produced character, e.g. "#" for Ctrl+Shift+3 on a US
// layout) - main derives the accelerator from the physical key so the result doesn't
// change depending on whether Shift is held. It then displays back exactly what main
// would save - so the user sees the real accelerator, in Electron's naming, before saving
// it, and an unusable combination (or a physical key Electron has no name for at all) is
// flagged immediately rather than only as a save error.
input.addEventListener("keydown", async (e) => {
  // Tab must keep moving focus - the field is readonly-by-design (it captures raw keys rather than
  // accepting typed text), but that must not turn it into a keyboard trap.
  if (e.key === "Tab") return;
  // A bare Enter (no modifiers) submits the form, matching every other input in the app, rather than
  // being built into a one-key "accelerator" that isValidAccelerator then rejects as needing a modifier.
  // Enter held WITH a modifier (e.g. Shift+Enter) is a legitimate accelerator and falls through below.
  if (e.key === "Enter" && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
    e.preventDefault();
    void submit();
    return;
  }
  e.preventDefault();
  const descriptor = {
    ctrlKey: e.ctrlKey,
    metaKey: e.metaKey,
    altKey: e.altKey,
    shiftKey: e.shiftKey,
    code: e.code,
  };
  const { accelerator, valid, message } = await window.hotkeyConfig.describe(descriptor);
  pending = accelerator;
  input.value = accelerator;
  error.textContent = valid ? "" : message || "Use at least one modifier (Ctrl, Alt, Shift) plus one key.";
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
