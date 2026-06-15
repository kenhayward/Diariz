import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    // Proxy API + SignalR to the .NET backend during development.
    proxy: {
      "/api": { target: "http://localhost:8080", changeOrigin: true },
      "/hubs": { target: "http://localhost:8080", ws: true, changeOrigin: true },
    },
  },
});
