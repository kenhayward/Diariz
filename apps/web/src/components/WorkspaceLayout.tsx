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

/// Full-height app frame: the three-panel workspace with a status bar locked to the bottom (a shrink-0 flex
/// child, so it never scrolls while the panels scroll internally). There is no brand header - the meetings
/// panel runs to the top of the window and the capture bar sits inside the content column (Workspace.tsx).
/// UploadProvider spans the workspace so the capture bar's Upload button and the recordings drop zone share
/// one queue. StatusProvider spans the workspace + status bar so routed pages can push progress messages the
/// bar shows. HubPopoverProvider spans the workspace so the capture bar and the account menu share one open
/// popover. TourProvider drives the first-run guided tour (TourOverlay renders on top when active).
export default function WorkspaceLayout() {
  return (
    <RoomProvider>
      <UploadProvider>
        <StatusProvider>
          <TourProvider>
            <ToastProvider>
              <div className="flex h-screen flex-col bg-gray-50 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
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
