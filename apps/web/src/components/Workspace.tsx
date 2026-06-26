import { useRef, useState } from "react";
import { Outlet } from "react-router-dom";
import RecordingsPanel from "./RecordingsPanel";
import ChatPanel from "./ChatPanel";
import { SelectionProvider } from "../lib/selection";

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
  const [leftOpen, setLeftOpen] = usePersistedBool("diariz.panels.left", true);
  const [rightOpen, setRightOpen] = usePersistedBool("diariz.panels.right", false);

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
        <aside className="flex w-72 shrink-0 flex-col border-r bg-white dark:border-gray-700 dark:bg-gray-900">
          <PanelHeader title="Recordings" onCollapse={() => setLeftOpen(false)} chevron="◀" />
          <div className="min-h-0 flex-1 overflow-y-auto">
            <RecordingsPanel />
          </div>
        </aside>
      ) : (
        <CollapsedRail label="Recordings" onExpand={() => setLeftOpen(true)} chevron="▶" />
      )}

      <main className="min-w-0 flex-1 overflow-y-auto bg-gray-50 dark:bg-gray-950">
        <div className="p-6">
          <Outlet />
        </div>
      </main>

      {/* The chat panel stays mounted even when collapsed (hidden via CSS) so its conversation
          state survives collapse/expand. The rail is shown alongside when collapsed. */}
      <div className={rightOpen ? "flex shrink-0" : "hidden"}>
        <div
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize chat panel"
          onMouseDown={startResize}
          className="w-1 cursor-col-resize bg-transparent transition-colors hover:bg-blue-400 dark:hover:bg-blue-600"
        />
        <aside
          style={{ width: rightWidth }}
          className="flex shrink-0 flex-col border-l bg-white dark:border-gray-700 dark:bg-gray-900"
        >
          <PanelHeader title="Chat" onCollapse={() => setRightOpen(false)} chevron="▶" />
          <div className="min-h-0 flex-1 overflow-y-auto">
            <ChatPanel />
          </div>
        </aside>
      </div>
      {!rightOpen && <CollapsedRail label="Chat" onExpand={() => setRightOpen(true)} chevron="◀" />}
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
  return (
    <div className="flex h-9 shrink-0 items-center justify-between border-b px-3 dark:border-gray-700">
      <span className="text-xs font-medium uppercase tracking-wide text-gray-400 dark:text-gray-500">
        {title}
      </span>
      <button
        type="button"
        aria-label={`Collapse ${title} panel`}
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
}: {
  label: string;
  onExpand: () => void;
  chevron: string;
}) {
  return (
    <div className="flex w-9 shrink-0 flex-col items-center border-r bg-white py-2 dark:border-gray-700 dark:bg-gray-900">
      <button
        type="button"
        aria-label={`Expand ${label} panel`}
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
