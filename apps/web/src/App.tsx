import { Navigate, Route, Routes, Link } from "react-router-dom";
import { useAuth } from "./auth";
import Login from "./pages/Login";
import Recordings from "./pages/Recordings";
import RecordingDetail from "./pages/RecordingDetail";

function Shell({ children }: { children: React.ReactNode }) {
  const { isAuthed, logout } = useAuth();
  return (
    <div className="min-h-screen bg-gray-50 text-gray-900">
      <header className="flex items-center justify-between border-b bg-white px-6 py-3">
        <Link to="/" className="text-xl font-semibold">
          Diariz
        </Link>
        {isAuthed && (
          <button onClick={logout} className="text-sm text-gray-500 hover:text-gray-900">
            Sign out
          </button>
        )}
      </header>
      <main className="mx-auto max-w-4xl px-6 py-6">{children}</main>
    </div>
  );
}

function RequireAuth({ children }: { children: React.ReactNode }) {
  const { isAuthed } = useAuth();
  return isAuthed ? <>{children}</> : <Navigate to="/login" replace />;
}

export default function App() {
  return (
    <Shell>
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route
          path="/"
          element={
            <RequireAuth>
              <Recordings />
            </RequireAuth>
          }
        />
        <Route
          path="/recordings/:id"
          element={
            <RequireAuth>
              <RecordingDetail />
            </RequireAuth>
          }
        />
      </Routes>
    </Shell>
  );
}
