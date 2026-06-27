import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

// App version: the repo-root version.json is canonical, but it's outside the Docker build context,
// so fall back to this package's version (the rule keeps both in sync), or an APP_VERSION build arg.
function appVersion(): string {
  if (process.env.APP_VERSION) return process.env.APP_VERSION;
  for (const rel of ["../../version.json", "./package.json"]) {
    try {
      return JSON.parse(readFileSync(fileURLToPath(new URL(rel, import.meta.url)), "utf8")).version as string;
    } catch {
      /* try the next source */
    }
  }
  return "0.0.0";
}
const version = appVersion();

// Short build commit — best-effort (BUILD_COMMIT env, else git, else blank for e.g. Docker builds).
function buildCommit(): string {
  if (process.env.BUILD_COMMIT) return process.env.BUILD_COMMIT;
  try {
    return execFileSync("git", ["rev-parse", "--short", "HEAD"], { stdio: ["ignore", "pipe", "ignore"] })
      .toString()
      .trim();
  } catch {
    return "";
  }
}

export default defineConfig({
  plugins: [react(), tailwindcss()],
  define: {
    __APP_VERSION__: JSON.stringify(version),
    __BUILD_COMMIT__: JSON.stringify(buildCommit()),
  },
  server: {
    port: 5173,
    // Proxy API + SignalR to the .NET backend during development.
    proxy: {
      "/api": { target: "http://localhost:8080", changeOrigin: true },
      "/hubs": { target: "http://localhost:8080", ws: true, changeOrigin: true },
    },
  },
});
