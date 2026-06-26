import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// Kept separate from vite.config.ts so the production build does not depend on vitest.
export default defineConfig({
  plugins: [react()], // compile JSX/TSX so React components can be rendered in tests
  test: {
    environment: "jsdom", // provides window / localStorage for the api + token helpers
    globals: true, // enables @testing-library/react's automatic cleanup between tests
    include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
  },
});
