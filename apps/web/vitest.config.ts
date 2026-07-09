import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const version = JSON.parse(
  readFileSync(fileURLToPath(new URL("../../version.json", import.meta.url)), "utf8"),
).version as string;

// Kept separate from vite.config.ts so the production build does not depend on vitest.
export default defineConfig({
  plugins: [react()], // compile JSX/TSX so React components can be rendered in tests
  // Mirror the build-time globals so version-aware components/tests have them.
  define: {
    __APP_VERSION__: JSON.stringify(version),
    __BUILD_COMMIT__: JSON.stringify("test"),
  },
  test: {
    environment: "jsdom", // provides window / localStorage for the api + token helpers
    globals: true, // enables @testing-library/react's automatic cleanup between tests
    setupFiles: ["./src/test-setup.ts"], // initialise i18next once for component tests
    include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
    // The self-hosted CI runner is resource-constrained (the full suite takes ~60s there vs ~8s locally),
    // so the 5s default per-test timeout flakes under contention. Raise the ceiling well above any real
    // render time; a genuine hang still fails, just later.
    testTimeout: 20000,
    hookTimeout: 20000,
  },
});
