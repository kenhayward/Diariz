const test = require("node:test");
const assert = require("node:assert/strict");
const { deviceIdFor } = require("./deviceId");

/// Minimal stand-in for electron-store.
function fakeStore(initial = {}) {
  const data = { ...initial };
  return {
    get: (k) => data[k],
    set: (k, v) => {
      data[k] = v;
    },
    all: () => data,
  };
}

test("deviceIdFor mints an id once and reuses it", () => {
  const store = fakeStore();
  const first = deviceIdFor(store);

  assert.equal(typeof first, "string");
  assert.ok(first.length > 0);
  // Stability is the whole point: a new id every launch would create a fresh source row each time, and each
  // one's orphan sweep would fight the others.
  assert.equal(deviceIdFor(store), first);
  assert.equal(store.get("outlookDeviceId"), first);
});

test("deviceIdFor keeps an id that is already stored", () => {
  const store = fakeStore({ outlookDeviceId: "existing-id" });
  assert.equal(deviceIdFor(store), "existing-id");
});

test("deviceIdFor replaces a blank or missing value", () => {
  assert.notEqual(deviceIdFor(fakeStore({ outlookDeviceId: "" })), "");
  assert.ok(deviceIdFor(fakeStore({ outlookDeviceId: null })).length > 0);
});

test("two installations get different ids", () => {
  assert.notEqual(deviceIdFor(fakeStore()), deviceIdFor(fakeStore()));
});
