"use strict";

// Pure model for the right-click context menu. Electron enables Chromium's spellchecker by default -
// which is why misspellings are underlined - but ships no context menu of its own: the right-click menu
// is part of Chrome's browser UI, not the content layer Electron embeds. So the squiggle appears and
// clicking it does nothing until the app builds the menu itself from the `context-menu` event's params
// (issue #678). `main.js` owns that event and the Menu/webContents calls; the shape of the menu lives
// here so it can be unit-tested without an Electron window.
//
// It doubles as the app's only cut/copy/paste on Windows, which runs menu-less
// (`Menu.setApplicationMenu(null)`); macOS has the `editMenu` role as well.

/// How many spelling suggestions to show. Chromium's own menu shows a handful; the cap matters because
/// the list is drawn at the cursor, so an unbounded one can run off the bottom of the screen.
const MAX_SUGGESTIONS = 5;

/// Build the menu for one right-click, as plain descriptors `main.js` turns into a Menu template:
///
///   { label, action: "replace-misspelling", replacement }  -> webContents.replaceMisspelling(replacement)
///   { label, action: "add-to-dictionary", word }           -> session.addWordToSpellCheckerDictionary(word)
///   { label, enabled: false }                              -> an inert placeholder
///   { role, enabled }                                      -> a built-in Electron role
///   { type: "separator" }
///
/// Returns an **empty array** when there is nothing worth offering, which `main.js` reads as "pop no
/// menu": a menu whose every item is greyed out is worse than the no-op the user already has.
///
/// `params` is Electron's context-menu params (misspelledWord, dictionarySuggestions, isEditable,
/// selectionText, editFlags). Each field is defaulted, so a params object from an older Electron - or a
/// surface that does not populate editFlags - degrades to an enabled menu rather than a dead one.
function contextMenuItems(params) {
  const {
    isEditable = false,
    selectionText = "",
    misspelledWord = "",
    dictionarySuggestions = [],
    editFlags = {},
  } = params ?? {};

  const can = (flag) => editFlags[flag] !== false;
  const items = [];

  // `misspelledWord` is only ever set on an editable field, so the spelling block implies the edit
  // roles below it.
  if (misspelledWord) {
    const suggestions = dictionarySuggestions.slice(0, MAX_SUGGESTIONS);
    if (suggestions.length > 0) {
      for (const replacement of suggestions) {
        items.push({ label: replacement, action: "replace-misspelling", replacement });
      }
    } else {
      items.push({ label: "No spelling suggestions", enabled: false });
    }
    items.push({ type: "separator" });
    items.push({ label: "Add to dictionary", action: "add-to-dictionary", word: misspelledWord });
    items.push({ type: "separator" });
  }

  if (isEditable) {
    items.push({ role: "cut", enabled: can("canCut") });
    items.push({ role: "copy", enabled: can("canCopy") });
    items.push({ role: "paste", enabled: can("canPaste") });
    items.push({ type: "separator" });
    items.push({ role: "selectAll", enabled: can("canSelectAll") });
    return items;
  }

  // Read-only: copy is the only thing that means anything, and only with a selection to copy. Select
  // all is deliberately absent - it would select the whole page rather than the block clicked in.
  if (selectionText) items.push({ role: "copy", enabled: can("canCopy") });

  return items;
}

module.exports = { contextMenuItems, MAX_SUGGESTIONS };
