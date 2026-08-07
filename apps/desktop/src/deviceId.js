const { randomUUID } = require("node:crypto");

/// This installation's opaque id, minted once and kept in the app's own config.
///
/// It is what makes the server's Outlook mirror per-machine rather than per-user: two PCs syncing the same
/// mailbox must be independent, or each one's orphan sweep would delete the other's meetings on every launch.
/// Stable across restarts and upgrades; a fresh install is legitimately a new device.
///
/// Takes the store rather than importing it, so it is testable without electron-store.
function deviceIdFor(store) {
  const existing = store.get("outlookDeviceId");
  if (typeof existing === "string" && existing.length > 0) return existing;

  const id = randomUUID();
  store.set("outlookDeviceId", id);
  return id;
}

module.exports = { deviceIdFor };
