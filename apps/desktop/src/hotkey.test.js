"use strict";

// hotkey.js is a plain renderer script (getElementById + window.hotkeyConfig at module scope, no
// module.exports - it's sandboxed and can't require local modules, see its own top-of-file comment).
// To unit-test its keydown/save behaviour without pulling in a DOM library, each test installs a
// minimal fake `document`/`window` as globals, then requires the module fresh (its top-level code runs
// once at require time, exactly like a `<script>` tag would).

const test = require("node:test");
const assert = require("node:assert");

function makeElement() {
  const listeners = {};
  return {
    value: "",
    textContent: "",
    disabled: false,
    addEventListener(type, cb) {
      (listeners[type] ||= []).push(cb);
    },
    fire(type, evt) {
      for (const cb of listeners[type] || []) cb(evt);
    },
  };
}

function fakeEvent(overrides = {}) {
  let defaultPrevented = false;
  return {
    key: "",
    code: "",
    ctrlKey: false,
    metaKey: false,
    altKey: false,
    shiftKey: false,
    preventDefault() {
      defaultPrevented = true;
    },
    get defaultPrevented() {
      return defaultPrevented;
    },
    ...overrides,
  };
}

// Installs the fake DOM as globals and requires a fresh copy of hotkey.js (its top-level code runs
// immediately, exactly like a <script> tag would). The globals are deliberately left in place after
// requiring - the module's listeners close over the `window`/`document` identifiers themselves and
// resolve them at *call* time, not at closure-creation time, so they must still exist whenever a test
// later fires a keydown/click. Each call overwrites both with a fresh set, so tests don't bleed into
// each other; `node --test` isolates test files into separate processes, so nothing here leaks further.
function load({ load: loadImpl, save: saveImpl, describe: describeImpl } = {}) {
  const elements = { combo: makeElement(), error: makeElement(), save: makeElement() };
  let closed = false;
  global.document = { getElementById: (id) => elements[id] };
  global.window = {
    close: () => {
      closed = true;
    },
    hotkeyConfig: {
      load: loadImpl || (() => new Promise(() => {})), // never resolves unless a test wants it to
      save: saveImpl || (async () => ({ ok: true })),
      describe: describeImpl || (async () => ({ accelerator: "X", valid: true })),
    },
  };
  const modPath = require.resolve("./hotkey.js");
  delete require.cache[modPath];
  require(modPath);
  return { elements, isClosed: () => closed };
}

test("Save starts disabled and stays disabled before the initial load resolves", () => {
  const { elements } = load({ load: () => new Promise(() => {}) });
  assert.equal(elements.save.disabled, true);
});

test("Save becomes enabled once the initial load resolves, populated with the loaded value", async () => {
  let resolveLoad;
  const { elements } = load({ load: () => new Promise((res) => (resolveLoad = res)) });
  assert.equal(elements.save.disabled, true);

  resolveLoad("Control+Shift+9");
  await Promise.resolve();
  await Promise.resolve();

  assert.equal(elements.save.disabled, false);
  assert.equal(elements.combo.value, "Control+Shift+9");
});

test("Tab is not swallowed - preventDefault is not called, so focus can leave the field", () => {
  const { elements } = load();
  const evt = fakeEvent({ key: "Tab", code: "Tab" });
  elements.combo.fire("keydown", evt);
  assert.equal(evt.defaultPrevented, false);
});

test("Shift+Tab is likewise not swallowed", () => {
  const { elements } = load();
  const evt = fakeEvent({ key: "Tab", code: "Tab", shiftKey: true });
  elements.combo.fire("keydown", evt);
  assert.equal(evt.defaultPrevented, false);
});

test("a bare Enter submits the form instead of being treated as a one-key accelerator", async () => {
  let saved = null;
  const { elements, isClosed } = load({
    load: () => Promise.resolve("Control+Shift+9"),
    save: async (accelerator) => {
      saved = accelerator;
      return { ok: true };
    },
  });
  await Promise.resolve();
  await Promise.resolve();

  const evt = fakeEvent({ key: "Enter", code: "Enter" });
  elements.combo.fire("keydown", evt);
  // submit() is fired-and-forgotten (void submit()) from inside the handler; flush its microtasks.
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();

  assert.equal(evt.defaultPrevented, true); // no stray newline in the readonly field
  assert.equal(saved, "Control+Shift+9"); // saved the already-loaded value, not null
  assert.equal(isClosed(), true); // window.close() ran, same as a Save-button click
});

test("Enter held with a modifier is still captured as a real accelerator, not a submit", () => {
  let describedWith = null;
  const { elements } = load({
    describe: async (descriptor) => {
      describedWith = descriptor;
      return { accelerator: "Shift+Enter", valid: true };
    },
  });

  const evt = fakeEvent({ key: "Enter", code: "Enter", shiftKey: true });
  elements.combo.fire("keydown", evt);

  assert.equal(evt.defaultPrevented, true);
  assert.ok(describedWith); // went through the normal capture path, not the submit shortcut
  assert.equal(describedWith.shiftKey, true);
});

test("clicking Save before the initial load resolves cannot happen - the button is disabled", () => {
  // Regression guard for the "Save sends null" bug: nothing calls the click listener directly (a real
  // disabled button never dispatches a click), so this asserts the state that prevents it.
  const { elements } = load({ load: () => new Promise(() => {}) });
  assert.equal(elements.save.disabled, true);
});
