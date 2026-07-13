import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Outlet } from "react-router-dom";
import RouteErrorBoundary from "./RouteErrorBoundary";
import RecordingsPanel from "./RecordingsPanel";
import ChatPanel from "./ChatPanel";
import RoomSwitcher from "./RoomSwitcher";
import { SelectionProvider } from "../lib/selection";
import { useRoom } from "../lib/rooms";
import { useResizableWidth } from "../lib/useResizableWidth";

function usePersistedBool(key: string, fallback: boolean): [boolean, (v: boolean) => void] {
  const [value, setValue] = useState<boolean>(() => {
    const stored = localStorage.getItem(key);
    return stored === null ? fallback : stored === "true";
  });
  return [
    value,
    (v: boolean) => {
      localStorage.setItem(key, String(v));
      setValue(v);
    },
  ];
}

const RIGHT_WIDTH_KEY = "diariz.panels.rightWidth";
const RIGHT_MIN = 260;
const RIGHT_MAX = 640;

/// The three-panel workspace: recordings list · selected recording (Outlet) · chat.
/// Left and right panels collapse to a thin rail; the right panel is also drag-resizable.
export default function Workspace() {
  const { t } = useTranslation("workspace");
  const { currentRoom } = useRoom();
  const [leftOpen, setLeftOpen] = usePersistedBool("diariz.panels.left", true);
  const [rightOpen, setRightOpen] = usePersistedBool("diariz.panels.right", false);
  const { width: leftWidth, startResize: startLeftResize } = useResizableWidth("diariz.panels.leftWidth", {
    min: 200,
    max: 560,
    initial: 288,
  });

  const [rightWidth, setRightWidth] = useState<number>(() => {
    const stored = Number(localStorage.getItem(RIGHT_WIDTH_KEY));
    return stored >= RIGHT_MIN && stored <= RIGHT_MAX ? stored : 320;
  });
  const widthRef = useRef(rightWidth);
  widthRef.current = rightWidth;

  function startResize(e: React.MouseEvent) {
    e.preventDefault();
    const onMove = (ev: MouseEvent) =>
      setRightWidth(Math.min(RIGHT_MAX, Math.max(RIGHT_MIN, window.innerWidth - ev.clientX)));
    const onUp = () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
      document.body.style.userSelect = "";
      localStorage.setItem(RIGHT_WIDTH_KEY, String(widthRef.current));
    };
    document.body.style.userSelect = "none"; // don't select text while dragging
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
  }

  return (
    <SelectionProvider>
    <div className="flex min-h-0 flex-1">
      {leftOpen ? (
        <>
          <aside
            data-tour="recordings"
            style={{ width: leftWidth }}
            className="flex shrink-0 flex-col border-r bg-white dark:border-gray-700 dark:bg-gray-900"
          >
            {/* A crash in the sidebar is contained here, so the detail panel (e.g. an opened folder) still
                renders instead of the whole app going blank (#289). */}
            <RouteErrorBoundary>
              <RoomSwitcher onCollapse={() => setLeftOpen(false)} chevron="◀" />
              <div className="min-h-0 flex-1 overflow-y-auto">
                <RecordingsPanel />
              </div>
            </RouteErrorBoundary>
          </aside>
          <div
            role="separator"
            aria-orientation="vertical"
            aria-label={t("resizeRecordings")}
            onMouseDown={(e) => startLeftResize(e, "left")}
            className="w-1 shrink-0 cursor-col-resize bg-transparent transition-colors hover:bg-blue-400 dark:hover:bg-blue-600"
          />
        </>
      ) : (
        <CollapsedRail label={currentRoom?.name ?? t("panelMeetings")} onExpand={() => setLeftOpen(true)} chevron="▶" tour="recordings" />
      )}

      <main data-tour="detail" className="min-w-0 flex-1 overflow-y-auto bg-gray-50 dark:bg-gray-950">
        <div className="p-6">
          {/* Contain a routed-page crash so it shows a message instead of blanking the whole app (#289). */}
          <RouteErrorBoundary>
            <Outlet />
          </RouteErrorBoundary>
        </div>
      </main>

      {/* The chat panel stays mounted even when collapsed (hidden via CSS) so its conversation
          state survives collapse/expand. The rail is shown alongside when collapsed. */}
      <div data-tour="chat" className={rightOpen ? "flex shrink-0" : "hidden"}>
        <div
          role="separator"
          aria-orientation="vertical"
          aria-label={t("resizeChat")}
          onMouseDown={startResize}
          className="w-1 cursor-col-resize bg-transparent transition-colors hover:bg-blue-400 dark:hover:bg-blue-600"
        />
        <aside
          style={{ width: rightWidth }}
          className="flex shrink-0 flex-col border-l bg-white dark:border-gray-700 dark:bg-gray-900"
        >
          <PanelHeader title={t("panelChat")} onCollapse={() => setRightOpen(false)} chevron="▶" />
          <div className="min-h-0 flex-1 overflow-y-auto">
            {/* Contain a chat-panel crash so it doesn't blank the detail panel or the whole app (#289). */}
            <RouteErrorBoundary>
              <ChatPanel />
            </RouteErrorBoundary>
          </div>
        </aside>
      </div>
      {!rightOpen && <CollapsedRail label={t("panelChat")} onExpand={() => setRightOpen(true)} chevron="◀" tour="chat" />}
    </div>
    </SelectionProvider>
  );
}

function PanelHeader({
  title,
  onCollapse,
  chevron,
}: {
  title: string;
  onCollapse: () => void;
  chevron: string;
}) {
  const { t } = useTranslation("workspace");
  return (
    <div className="flex h-9 shrink-0 items-center justify-between border-b px-3 dark:border-gray-700">
      <span className="text-xs font-medium uppercase tracking-wide text-gray-400 dark:text-gray-500">
        {title}
      </span>
      <button
        type="button"
        aria-label={t("collapsePanel", { title })}
        onClick={onCollapse}
        className="rounded px-1 text-xs text-gray-400 hover:bg-gray-100 hover:text-gray-700 dark:hover:bg-gray-800 dark:hover:text-gray-200"
      >
        {chevron}
      </button>
    </div>
  );
}

function CollapsedRail({
  label,
  onExpand,
  chevron,
  tour,
}: {
  label: string;
  onExpand: () => void;
  chevron: string;
  tour?: string;
}) {
  const { t } = useTranslation("workspace");
  return (
    <div
      data-tour={tour}
      className="flex w-9 shrink-0 flex-col items-center border-r bg-white py-2 dark:border-gray-700 dark:bg-gray-900"
    >
      <button
        type="button"
        aria-label={t("expandPanel", { label })}
        onClick={onExpand}
        className="rounded px-1 py-1 text-xs text-gray-400 hover:bg-gray-100 hover:text-gray-700 dark:hover:bg-gray-800 dark:hover:text-gray-200"
      >
        {chevron}
      </button>
      <span className="mt-2 text-[10px] uppercase tracking-wide text-gray-400 [writing-mode:vertical-rl] dark:text-gray-500">
        {label}
      </span>
    </div>
  );
}
