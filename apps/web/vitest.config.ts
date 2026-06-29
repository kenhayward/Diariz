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
  },
});
