import TopBar from "./TopBar";
import Workspace from "./Workspace";
import TourOverlay from "./TourOverlay";
import StatusBar from "./StatusBar";
import { UploadProvider } from "../lib/uploadContext";
import { TourProvider } from "../lib/tour";
import { StatusProvider } from "../lib/status";
import { RoomProvider } from "../lib/rooms";

/// Full-height app frame: persistent top bar over the three-panel workspace, with a status bar locked to the
/// bottom (a shrink-0 flex child, so it never scrolls while the panels scroll internally).
/// UploadProvider spans both so the Upload button (top bar) and the recordings drop zone share one queue.
/// StatusProvider spans the workspace + status bar so routed pages can push progress messages the bar shows.
/// TourProvider drives the first-run guided tour (TourOverlay renders on top when active).
export default function WorkspaceLayout() {
  return (
    <RoomProvider>
      <UploadProvider>
        <StatusProvider>
          <TourProvider>
            <div className="flex h-screen flex-col bg-gray-50 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
              <TopBar />
              <Workspace />
              <StatusBar />
            </div>
            <TourOverlay />
          </TourProvider>
        </StatusProvider>
      </UploadProvider>
    </RoomProvider>
  );
}
