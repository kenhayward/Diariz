import { defineConfig } from "vitest/config";

// Kept separate from vite.config.ts so the production build does not depend on vitest.
export default defineConfig({
  test: {
    environment: "jsdom", // provides window / localStorage for the api + token helpers
    include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
  },
});
