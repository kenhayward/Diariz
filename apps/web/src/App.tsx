import { useEffect, lazy, Suspense } from "react";
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
import SectionDetail from "./pages/SectionDetail";
import CalendarEventDetail from "./pages/CalendarEventDetail";
// Lazy-loaded: the Scalar API reference is a large bundle, only needed on /developers/api.
const ApiReference = lazy(() => import("./pages/ApiReference"));
import WorkspaceLayout from "./components/WorkspaceLayout";
import RouteErrorBoundary from "./components/RouteErrorBoundary";
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
        path="/developers/api"
        element={
          <RequireAuth>
            <Suspense fallback={null}>
              <ApiReference />
            </Suspense>
          </RequireAuth>
        }
      />
      <Route
        path="/"
        element={
          <RequireAuth>
            {/* A crash anywhere in the workspace (a panel, a provider, a routed page) shows a message
                instead of unmounting the whole app to a blank screen. See issue #289. */}
            <RouteErrorBoundary>
              <WorkspaceLayout />
            </RouteErrorBoundary>
          </RequireAuth>
        }
      >
        <Route index element={<EmptyDetail />} />
        <Route path="recordings/:id" element={<RecordingDetail />} />
        <Route path="sections/:id" element={<SectionDetail />} />
        <Route path="calendar-event/:eventId" element={<CalendarEventDetail />} />
        {/* The room lives in the URL. RoomProvider reads :roomId (via useMatch) to pick the current room; the
            children mirror the legacy ones above, which stay working as the personal-room default while only
            one room exists. Per-room link rewrites + query-key isolation land in Phase 4, where a real second
            room makes them observable and testable. */}
        <Route path="rooms/:roomId">
          <Route index element={<EmptyDetail />} />
          <Route path="recordings/:id" element={<RecordingDetail />} />
          <Route path="sections/:id" element={<SectionDetail />} />
          <Route path="calendar-event/:eventId" element={<CalendarEventDetail />} />
        </Route>
      </Route>
    </Routes>
  );
}
