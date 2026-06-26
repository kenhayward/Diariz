import TopBar from "./TopBar";
import Workspace from "./Workspace";

/// Full-height app frame: persistent top bar over the three-panel workspace.
export default function WorkspaceLayout() {
  return (
    <div className="flex h-screen flex-col bg-gray-50 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
      <TopBar />
      <Workspace />
    </div>
  );
}
