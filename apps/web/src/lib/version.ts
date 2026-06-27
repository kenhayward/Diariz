// App version + build commit, injected at build time (see vite.config.ts / vitest.config.ts).
// The version's single source of truth is the repo-root version.json.
export const APP_VERSION: string = __APP_VERSION__;
export const BUILD_COMMIT: string = __BUILD_COMMIT__;
