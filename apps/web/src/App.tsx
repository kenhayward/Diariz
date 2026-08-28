import { useEffect, lazy, Suspense } from "react";
import { Navigate, Route, Routes, useLocation, useSearchParams } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { useAuth } from "./auth";
import { useLanguage } from "./language";
import { record } from "./lib/trail";
import Login from "./pages/Login";
import GoogleCallback from "./pages/GoogleCallback";
import OAuthConsent from "./pages/OAuthConsent";
import RequestAccess from "./pages/RequestAccess";
import Setup from "./pages/Setup";
import Help from "./pages/Help";
import NotesPopout from "./pages/NotesPopout";
import RecordingDetail from "./pages/RecordingDetail";
import SectionDetail from "./pages/SectionDetail";
import CalendarEventDetail from "./pages/CalendarEventDetail";
// Lazy-loaded: carries the whole release archive, which is the bulk of the release history and has no
// business in the initial bundle - almost nobody opens this page, and everybody pays for it otherwise.
const ReleaseNotes = lazy(() => import("./pages/ReleaseNotes"));
// Lazy-loaded: the Scalar API reference is a large bundle, only needed on /developers/api.
const ApiReference = lazy(() => import("./pages/ApiReference"));
// Lazy-loaded: a Platform-Administrator-only page, no reason to ship it in the main bundle.
const LlmUsage = lazy(() => import("./pages/LlmUsage"));
const LlmModels = lazy(() => import("./pages/LlmModels"));
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

  // Feed the feedback trail on every route change. Only pathname - never search, which is where a
  // token would be (e.g. the Google callback's one-shot query param).
  const location = useLocation();
  useEffect(() => {
    record({ kind: "nav", label: location.pathname });
  }, [location.pathname]);

  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route path="/auth/google/callback" element={<GoogleCallback />} />
      <Route path="/oauth/consent" element={<OAuthConsent />} />
      <Route path="/request-access" element={<RequestAccess />} />
      <Route path="/setup" element={<Setup />} />
      <Route
        path="/release-notes"
        element={
          <Suspense fallback={null}>
            <ReleaseNotes />
          </Suspense>
        }
      />
      {/* Public, like the release notes: help is useful before signing in, and the contextual `?`
          popovers deep link into it from anywhere. */}
      <Route path="/help" element={<Help />} />
      <Route path="/help/:slug" element={<Help />} />
      {/* The desktop shell's detached live-notes window. Deliberately outside the workspace layout, so
          it mounts no sidebar, no recorder and no SignalR - and outside RequireAuth, because it holds no
          server data of its own: it renders nothing until the main window answers on the same-origin
          channel, and a login redirect inside a 380px window would be nonsense. */}
      <Route path="/notes-popout" element={<NotesPopout />} />
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
      {/* RequireAuth only checks that someone is signed in - the Platform Administrator gate lives inside
          LlmUsage itself, which renders a refusal instead of the table for anyone else. */}
      <Route
        path="/admin/llm-usage"
        element={
          <RequireAuth>
            <Suspense fallback={null}>
              <LlmUsage />
            </Suspense>
          </RequireAuth>
        }
      />
      {/* Same arrangement: RequireAuth proves only that someone is signed in, and LlmModels renders a
          refusal for anyone who is not a Platform Administrator. */}
      <Route
        path="/admin/llm-models"
        element={
          <RequireAuth>
            <Suspense fallback={null}>
              <LlmModels />
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
