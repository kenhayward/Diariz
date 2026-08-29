"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { contextMenuItems } = require("./contextMenu");

// Electron always supplies editFlags; these helpers keep the cases below readable.
const editable = (over = {}) => ({
  isEditable: true,
  selectionText: "",
  misspelledWord: "",
  dictionarySuggestions: [],
  editFlags: { canCut: false, canCopy: false, canPaste: true, canSelectAll: true },
  ...over,
});

test("nothing to offer means no menu at all", () => {
  // Right-click on plain page chrome: not editable, nothing selected. Popping an all-disabled menu
  // there would be worse than popping none.
  assert.deepEqual(
    contextMenuItems({
      isEditable: false,
      selectionText: "",
      misspelledWord: "",
      dictionarySuggestions: [],
      editFlags: { canCut: false, canCopy: false, canPaste: false, canSelectAll: true },
    }),
    [],
  );
  assert.deepEqual(contextMenuItems(undefined), []);
});

test("a misspelled word lists its suggestions first, each replacing the word", () => {
  const items = contextMenuItems(
    editable({ misspelledWord: "recieve", dictionarySuggestions: ["receive", "relieve"] }),
  );

  assert.deepEqual(items.slice(0, 2), [
    { label: "receive", action: "replace-misspelling", replacement: "receive" },
    { label: "relieve", action: "replace-misspelling", replacement: "relieve" },
  ]);
  assert.deepEqual(items[2], { type: "separator" });
  assert.deepEqual(items[3], { label: "Add to dictionary", action: "add-to-dictionary", word: "recieve" });
});

test("a misspelled word with no suggestions still offers the dictionary", () => {
  const items = contextMenuItems(editable({ misspelledWord: "diariz", dictionarySuggestions: [] }));

  // A disabled placeholder rather than a silent gap, so the menu explains itself instead of looking
  // like the suggestions failed to load.
  assert.deepEqual(items[0], { label: "No spelling suggestions", enabled: false });
  assert.ok(items.some((i) => i.action === "add-to-dictionary"));
});

test("suggestions are capped so a long list cannot run off the screen", () => {
  const many = ["a", "b", "c", "d", "e", "f", "g", "h"];
  const items = contextMenuItems(editable({ misspelledWord: "x", dictionarySuggestions: many }));

  assert.deepEqual(
    items.filter((i) => i.action === "replace-misspelling").map((i) => i.replacement),
    ["a", "b", "c", "d", "e"],
  );
});

test("an editable field always offers the edit roles, enabled per editFlags", () => {
  const items = contextMenuItems(
    editable({ editFlags: { canCut: true, canCopy: true, canPaste: false, canSelectAll: true } }),
  );

  // Windows runs menu-less, so this is the only cut/copy/paste the app has.
  assert.deepEqual(items, [
    { role: "cut", enabled: true },
    { role: "copy", enabled: true },
    { role: "paste", enabled: false },
    { type: "separator" },
    { role: "selectAll", enabled: true },
  ]);
});

test("selected text outside an editable field offers copy only", () => {
  const items = contextMenuItems({
    isEditable: false,
    selectionText: "some transcript text",
    misspelledWord: "",
    dictionarySuggestions: [],
    editFlags: { canCut: false, canCopy: true, canPaste: false, canSelectAll: true },
  });

  // No cut or paste: there is nothing to edit. Select all is equally meaningless on a read-only
  // selection, and would select the whole page rather than the block the user clicked in.
  assert.deepEqual(items, [{ role: "copy", enabled: true }]);
});

test("missing editFlags degrade to enabled rather than a dead menu", () => {
  const items = contextMenuItems({ isEditable: true, selectionText: "", misspelledWord: "" });

  assert.deepEqual(items, [
    { role: "cut", enabled: true },
    { role: "copy", enabled: true },
    { role: "paste", enabled: true },
    { type: "separator" },
    { role: "selectAll", enabled: true },
  ]);
});
