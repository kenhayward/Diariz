import TopBar from "./TopBar";
import Workspace from "./Workspace";
import { UploadProvider } from "../lib/uploadContext";

/// Full-height app frame: persistent top bar over the three-panel workspace.
/// UploadProvider spans both so the Upload button (top bar) and the recordings drop zone share one queue.
export default function WorkspaceLayout() {
  return (
    <UploadProvider>
      <div className="flex h-screen flex-col bg-gray-50 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
        <TopBar />
        <Workspace />
      </div>
    </UploadProvider>
  );
}
