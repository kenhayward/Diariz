import TopBar from "./TopBar";
import Workspace from "./Workspace";
import TourOverlay from "./TourOverlay";
import { UploadProvider } from "../lib/uploadContext";
import { TourProvider } from "../lib/tour";

/// Full-height app frame: persistent top bar over the three-panel workspace.
/// UploadProvider spans both so the Upload button (top bar) and the recordings drop zone share one queue.
/// TourProvider drives the first-run guided tour (TourOverlay renders on top when active).
export default function WorkspaceLayout() {
  return (
    <UploadProvider>
      <TourProvider>
        <div className="flex h-screen flex-col bg-gray-50 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
          <TopBar />
          <Workspace />
        </div>
        <TourOverlay />
      </TourProvider>
    </UploadProvider>
  );
}
