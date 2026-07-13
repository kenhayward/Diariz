import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { renderMarkdown } from "../lib/markdown";
import type { FormulaResult } from "../lib/types";
import FormulasManager from "./FormulasManager";

const WIDTH_KEY = "diariz.formulas.listWidth";
const MIN = 200,
  MAX = 460,
  INITIAL = 280;

/// The Formulas tab body: a resizable left runs-list (FormulasManager) beside a right panel that renders the
/// selected result's Markdown read-only. The empty state (no runs) spans the whole width. Resize is
/// delta-based (width += cursor delta) rather than useResizableWidth's absolute clientX, because this panel
/// is offset inside the detail column, not anchored to the viewport's left edge.
export default function FormulasPanel({
  recordingId,
  results,
  selectedId,
  onSelect,
}: {
  recordingId: string;
  results: FormulaResult[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
}) {
  const [width, setWidth] = useState<number>(() => {
    const s = Number(localStorage.getItem(WIDTH_KEY));
    return s >= MIN && s <= MAX ? s : INITIAL;
  });
  const widthRef = useRef(width);
  widthRef.current = width;

  function startResize(e: React.MouseEvent) {
    e.preventDefault();
    const startX = e.clientX;
    const startW = widthRef.current;
    const onMove = (ev: MouseEvent) => setWidth(Math.min(MAX, Math.max(MIN, startW + ev.clientX - startX)));
    const onUp = () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
      document.body.style.userSelect = "";
      localStorage.setItem(WIDTH_KEY, String(widthRef.current));
    };
    document.body.style.userSelect = "none";
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
  }

  // No runs yet: show the manager's empty state across the whole body (no split).
  if (results.length === 0) {
    return <FormulasManager results={results} selectedId={selectedId} onSelect={onSelect} />;
  }

  return (
    <div className="flex items-stretch">
      <div style={{ width }} className="min-w-0 shrink-0">
        <FormulasManager results={results} selectedId={selectedId} onSelect={onSelect} />
      </div>
      <div
        role="separator"
        aria-orientation="vertical"
        onMouseDown={startResize}
        className="w-1 shrink-0 cursor-col-resize bg-gray-200 hover:bg-blue-400 dark:bg-gray-700"
      />
      <div className="min-w-0 flex-1 pl-3">
        <ResultView recordingId={recordingId} selected={results.find((r) => r.id === selectedId) ?? null} />
      </div>
    </div>
  );
}

function ResultView({ recordingId, selected }: { recordingId: string; selected: FormulaResult | null }) {
  const { t } = useTranslation(["workspace", "common"]);
  // Only Ready results have a body to fetch; Generating/Failed results render a status message instead.
  const isReady = selected?.status === "Ready";
  const { data, isLoading, error } = useQuery({
    queryKey: ["formula-result-text", recordingId, selected?.id],
    queryFn: () => api.getFormulaResultText(recordingId, selected!.id),
    enabled: selected != null && isReady,
  });

  if (selected == null)
    return <p className="px-1 py-6 text-sm text-gray-400 dark:text-gray-500">{t("formulaSelectToView")}</p>;
  if (selected.status === "Generating")
    return <p className="px-1 py-6 text-sm text-gray-500 dark:text-gray-400">{t("formulaGenerating")}</p>;
  if (selected.status === "Failed")
    return (
      <p className="px-1 py-6 text-sm text-red-600 dark:text-red-400">
        {selected.error ? `${t("formulaFailed")}: ${selected.error}` : t("formulaFailed")}
      </p>
    );
  if (isLoading) return <p className="px-1 py-6 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>;
  if (error)
    return (
      <p className="px-1 py-6 text-sm text-red-600 dark:text-red-400">
        {apiErrorMessage(error, t("formulaResultLoadFailed"))}
      </p>
    );
  return (
    <div
      className="chat-md text-sm dark:text-gray-100"
      dangerouslySetInnerHTML={{ __html: renderMarkdown(data ?? "") }}
    />
  );
}
