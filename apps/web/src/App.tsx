import { Navigate, Route, Routes } from "react-router-dom";
import { useAuth } from "./auth";
import Login from "./pages/Login";
import RecordingDetail from "./pages/RecordingDetail";
import WorkspaceLayout from "./components/WorkspaceLayout";

function RequireAuth({ children }: { children: React.ReactNode }) {
  const { isAuthed } = useAuth();
  return isAuthed ? <>{children}</> : <Navigate to="/login" replace />;
}

function EmptyDetail() {
  return (
    <div className="flex h-full items-center justify-center pt-20 text-sm text-gray-400 dark:text-gray-500">
      Select a recording from the list.
    </div>
  );
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
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
