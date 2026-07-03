import { useEffect } from "react";
import { Navigate, Route, Routes, useSearchParams } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { useAuth } from "./auth";
import { useLanguage } from "./language";
import Login from "./pages/Login";
import GoogleCallback from "./pages/GoogleCallback";
import OAuthConsent from "./pages/OAuthConsent";
import RequestAccess from "./pages/RequestAccess";
import Setup from "./pages/Setup";
import ReleaseNotes from "./pages/ReleaseNotes";
import RecordingDetail from "./pages/RecordingDetail";
import WorkspaceLayout from "./components/WorkspaceLayout";
import EmptyDetail from "./components/EmptyDetail";

function RequireAuth({ children }: { children: React.ReactNode }) {
  const { isAuthed } = useAuth();
  return isAuthed ? <>{children}</> : <Navigate to="/login" replace />;
}

export default function App() {
  // An explicit `?lang=xx` overrides the active UI language (and is remembered) — highest priority in the
  // negotiation order. Ignored when there's no catalog for that language.
  const [params] = useSearchParams();
  const { setLanguage } = useLanguage();
  const qc = useQueryClient();
  const langParam = params.get("lang");
  useEffect(() => {
    if (langParam) setLanguage(langParam);
  }, [langParam, setLanguage]);

  // Returning from the Google data-access consent flow (or its error) — refresh the profile so the new
  // grants show, then strip the one-shot query param.
  const googleParam = params.get("google") ?? params.get("googleError");
  useEffect(() => {
    if (!googleParam) return;
    qc.invalidateQueries({ queryKey: ["user-profile"] });
    window.history.replaceState(null, "", window.location.pathname);
  }, [googleParam, qc]);

  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route path="/auth/google/callback" element={<GoogleCallback />} />
      <Route path="/oauth/consent" element={<OAuthConsent />} />
      <Route path="/request-access" element={<RequestAccess />} />
      <Route path="/setup" element={<Setup />} />
      <Route path="/release-notes" element={<ReleaseNotes />} />
      <Route
        path="/"
        element={
          <RequireAuth>
            <WorkspaceLayout />
          </RequireAuth>
        }
      >
        <Route index element={<EmptyDetail />} />
        <Route path="recordings/:id" element={<RecordingDetail />} />
      </Route>
    </Routes>
  );
}
