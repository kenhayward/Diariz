import TopBar from "./TopBar";
import Workspace from "./Workspace";
import TourOverlay from "./TourOverlay";
import StatusBar from "./StatusBar";
import ThemeSync from "./ThemeSync";
import OutlookSyncBridge from "./OutlookSyncBridge";
import { HubPopoverProvider } from "./hub/hubPopovers";
import { UploadProvider } from "../lib/uploadContext";
import { TourProvider } from "../lib/tour";
import { StatusProvider } from "../lib/status";
import { RoomProvider } from "../lib/rooms";
import { ToastProvider } from "../lib/toast";

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
            <ToastProvider>
              <div className="flex h-screen flex-col bg-gray-50 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
                <TopBar />
                {/* One popover open at a time across the recorder cluster and the account menu. They sit in
                    different subtrees now (the capture bar and the left panel's room row), so the context
                    has to span the whole workspace rather than one bar. */}
                <HubPopoverProvider>
                  <Workspace />
                </HubPopoverProvider>
                <StatusBar />
                {/* Renders nothing. Mounted here rather than in Preferences because a sync fires on launch
                    and from the tray, neither of which opens the settings window. A no-op in a browser. */}
                <OutlookSyncBridge />
                {/* Renders nothing: reconciles the server-persisted theme once signed in. */}
                <ThemeSync />
              </div>
              <TourOverlay />
            </ToastProvider>
          </TourProvider>
        </StatusProvider>
      </UploadProvider>
    </RoomProvider>
  );
}
