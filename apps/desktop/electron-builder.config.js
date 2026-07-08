"use strict";

// electron-builder configuration. The publish/update target is configurable so a fork can host
// its own builds:
//   default                                         → GitHub Releases of this repo
//   DIARIZ_PUBLISH=generic + DIARIZ_UPDATE_URL=...   → a self-hosted feed (e.g. your own server)
const generic = process.env.DIARIZ_PUBLISH === "generic";

// electron-builder only looks for `.git/config` in this package's own dir (apps/desktop),
// not the monorepo root, so it can't auto-detect owner/repo for the GitHub publish provider.
// In CI, derive them from the Actions environment (`GITHUB_REPOSITORY=owner/repo`) — this also
// keeps the repo fork-friendly: a fork's CI publishes to *its* Releases without editing this file.
const [ghOwner, ghRepo] = (process.env.GITHUB_REPOSITORY || "").split("/");

module.exports = {
  appId: "com.diariz.desktop",
  productName: "Diariz",
  protocols: [{ name: "Diariz", schemes: ["diariz"] }],
  directories: { output: "release", buildResources: "build" },
  // Loads the web app from the configured server, so the SPA itself isn't bundled.
  files: ["src/**/*", "!src/**/*.test.js", "build/icon.png", "build/trayTemplate.png", "build/trayTemplate@2x.png", "package.json"],
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
  // macOS (unsigned proof-of-concept). `identity: null` skips code signing so it builds without an Apple
  // Developer ID — the .dmg opens via right-click -> Open (Gatekeeper). Signing + notarization + a `zip`
  // (Squirrel.Mac auto-update) target + `arch: "universal"` come with the Apple Developer enrolment.
  // electron-builder converts the >=512px build/icon.png to .icns automatically.
  mac: {
    target: ["dmg"],
    icon: "build/icon.png",
    category: "public.app-category.productivity",
    identity: null,
    extendInfo: {
      // Required or macOS silently denies mic access. Screen Recording (for system-audio capture) has no
      // Info.plist string - it's a runtime TCC grant the user allows in System Settings on first capture.
      NSMicrophoneUsageDescription:
        "Diariz records your microphone to capture meetings for transcription.",
    },
  },
  publish: generic
    ? { provider: "generic", url: process.env.DIARIZ_UPDATE_URL || "https://example.invalid/updates/", channel: "latest" }
    : {
        provider: "github",
        releaseType: "release",
        ...(ghOwner && ghRepo ? { owner: ghOwner, repo: ghRepo } : {}),
      },
};
