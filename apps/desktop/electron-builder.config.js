"use strict";

// electron-builder configuration. The publish/update target is configurable so a fork can host
// its own builds:
//   default                                         → GitHub Releases of this repo
//   DIARIZ_PUBLISH=generic + DIARIZ_UPDATE_URL=...   → a self-hosted feed (e.g. your own server)
const generic = process.env.DIARIZ_PUBLISH === "generic";

module.exports = {
  appId: "com.diariz.desktop",
  productName: "Diariz",
  directories: { output: "release", buildResources: "build" },
  // Loads the web app from the configured server, so the SPA itself isn't bundled.
  files: ["src/**/*", "!src/**/*.test.js", "build/icon.png", "package.json"],
  win: {
    target: ["nsis"],
    icon: "build/icon.png",
  },
  nsis: {
    oneClick: false,
    perMachine: false,
    allowToChangeInstallationDirectory: true,
    shortcutName: "Diariz",
  },
  publish: generic
    ? { provider: "generic", url: process.env.DIARIZ_UPDATE_URL || "https://example.invalid/updates/", channel: "latest" }
    : { provider: "github", releaseType: "release" },
};
