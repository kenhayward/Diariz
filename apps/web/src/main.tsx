import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { BrowserRouter } from "react-router-dom";
import { AuthProvider } from "./auth";
import { ThemeProvider } from "./theme";
import { LanguageProvider } from "./language";
import { HelpProvider } from "./lib/help/HelpContext";
import { initTelemetry } from "./lib/telemetry";
import { installChunkReloadHandler } from "./lib/chunkReload";
import { installUnloadGuard } from "./lib/unloadGuard";
import App from "./App";
import "./index.css";

const queryClient = new QueryClient();

// A deploy replaces every content-hashed chunk, so a session that started before it asks for files the
// server no longer has the moment the user opens a page it had not loaded yet. Installed before first
// render, and outside it, because the failing import can happen at any point in the session's life - which
// for the tray app is days. See lib/chunkReload.
installChunkReloadHandler({ reload: () => window.location.reload(), storage: window.sessionStorage });

// Ask before the page is torn down mid-capture: a live recording exists only in this page's memory until
// the recorder stops. Browser only - the desktop shell hides to tray rather than unloading, and confirms its
// own Quit in the main process, because Electron cancels a close without showing anything. See lib/unloadGuard.
installUnloadGuard();

// Start reporting before the app renders, so a crash during first render is captured. Never blocks
// for long: initTelemetry resolves false rather than throwing if the config request fails, and it
// bounds that request with its own CONFIG_TIMEOUT_MS - so an API that accepts the connection and then
// hangs delays first paint by that timeout at most, not by the proxy's (60 s on nginx).
void initTelemetry().finally(() => {
  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <ThemeProvider>
          <LanguageProvider>
            <AuthProvider>
              <BrowserRouter>
                {/* Outside WorkspaceLayout so the contextual `?` buttons work on the standalone pages
                    (login, setup, help) as well as inside the workspace. Needs the router for its
                    "Read more" links. */}
                <HelpProvider>
                  <App />
                </HelpProvider>
              </BrowserRouter>
            </AuthProvider>
          </LanguageProvider>
        </ThemeProvider>
      </QueryClientProvider>
    </StrictMode>,
  );
});
