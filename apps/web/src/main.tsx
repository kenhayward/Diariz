import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { BrowserRouter } from "react-router-dom";
import { AuthProvider } from "./auth";
import { ThemeProvider } from "./theme";
import { LanguageProvider } from "./language";
import { HelpProvider } from "./lib/help/HelpContext";
import { initTelemetry } from "./lib/telemetry";
import App from "./App";
import "./index.css";

const queryClient = new QueryClient();

// Start reporting before the app renders, so a crash during first render is captured. Never blocks:
// initTelemetry resolves false rather than throwing if the config request fails.
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
