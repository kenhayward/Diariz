"use strict";

const input = document.getElementById("combo");
const error = document.getElementById("error");
const save = document.getElementById("save");

// The combination captured so far, in accelerator-ish form (e.g. "Control+Shift+9").
// Seeded from the persisted value once load() resolves, then replaced by every keydown.
let pending = null;

// A bare modifier keydown isn't a usable combination on its own - ignore it and wait
// for the following non-modifier key, matching isValidAccelerator's "one key" rule.
const MODIFIER_KEYS = new Set(["Control", "Shift", "Alt", "Meta"]);

window.hotkeyConfig.load().then((current) => {
  pending = current;
  input.value = current;
});

input.addEventListener("keydown", (e) => {
  e.preventDefault();
  if (MODIFIER_KEYS.has(e.key)) return;

  const mods = [];
  if (e.ctrlKey) mods.push("Control");
  if (e.metaKey) mods.push("Command");
  if (e.altKey) mods.push("Alt");
  if (e.shiftKey) mods.push("Shift");

  const key = e.key.length === 1 ? e.key.toUpperCase() : e.key;
  pending = [...mods, key].join("+");
  input.value = pending;
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
