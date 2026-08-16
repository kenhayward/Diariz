import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import type { LlmUsageOperationRow, LlmUsagePage, LlmUsageSortKey, LlmUsageTotals } from "../../lib/types";
import { formatDuration } from "../../lib/format";
import { kindLabel } from "./UsageFilterBar";

// The table's TOTAL column count (sortable + plain), used for a full-width colSpan on the loading/empty
// row - NOT the number of sortable columns (8 of these 12 are sortable; see the `header()`/`plainHeader()`
// calls below for the actual split).
const TOTAL_COLUMN_COUNT = 12;

/// The operations-mode results table: sortable headers, a totals row pinned under the header (never a sum
/// of the rows on screen - see `TotalsRow`), and a Previous/Next pager. Sorting is server-side (`onSort`
/// changes the query; there is no client-side re-ordering of whatever page is already loaded), and only
/// the keys `LlmUsageSortKey` allows are ever offered as headers - the API rejects anything else with 400.
export default function UsageTable({
  page,
  isLoading,
  isError,
  sort,
  desc,
  onSort,
  pageNumber,
  pageSize,
  onPageChange,
  basePath,
}: {
  page: LlmUsagePage<LlmUsageOperationRow> | undefined;
  isLoading: boolean;
  isError: boolean;
  sort: LlmUsageSortKey;
  desc: boolean;
  onSort: (key: LlmUsageSortKey) => void;
  pageNumber: number;
  pageSize: number;
  onPageChange: (page: number) => void;
  basePath: string;
}) {
  const { t } = useTranslation("account");

  if (isError) {
    return <p className="p-4 text-sm text-red-600 dark:text-red-400">{t("llmUsageLoadError")}</p>;
  }
  if (!page) {
    return <p className="p-4 text-sm text-gray-400 dark:text-gray-500">{t("common:loading")}</p>;
  }

  function header(key: LlmUsageSortKey, labelKey: string) {
    const active = sort === key;
    return (
      <th className="px-2 py-1 text-left font-medium text-gray-500 dark:text-gray-400">
        <button
          type="button"
          data-testid={`llm-usage-sort-${key}`}
          onClick={() => onSort(key)}
          className="flex items-center gap-1 hover:text-gray-800 dark:hover:text-gray-100"
        >
          {t(labelKey)}
          {active && <span aria-hidden="true">{desc ? "▼" : "▲"}</span>}
        </button>
      </th>
    );
  }

  function plainHeader(labelKey: string) {
    return <th className="px-2 py-1 text-left font-medium text-gray-500 dark:text-gray-400">{t(labelKey)}</th>;
  }

  return (
    <div className="flex h-full flex-col">
      <div className="min-h-0 flex-1 overflow-auto">
        <table className="w-full min-w-[1100px] table-auto border-collapse text-xs">
          <thead>
            <tr className="sticky top-0 z-20 border-b bg-white dark:border-gray-800 dark:bg-gray-900">
              {header("startedAt", "llmUsageColStarted")}
              {header("kind", "llmUsageColKind")}
              {header("userEmail", "llmUsageColUser")}
              {plainHeader("llmUsageColRecording")}
              {header("model", "llmUsageColModel")}
              {plainHeader("llmUsageColTurns")}
              {header("durationMs", "llmUsageColDuration")}
              {header("promptTokens", "llmUsageColPromptTokens")}
              {header("completionTokens", "llmUsageColCompletionTokens")}
              {plainHeader("llmUsageColReasoningTokens")}
              {header("totalTokens", "llmUsageColTotalTokens")}
              {plainHeader("llmUsageColOutcome")}
            </tr>
            {/* Pinned right under the header row - see TotalsRow for why it must render `page.totals`
                rather than folding over `page.rows`. */}
            <TotalsRow totals={page.totals} />
          </thead>
          <tbody>
            {isLoading ? (
              <tr>
                <td colSpan={TOTAL_COLUMN_COUNT} className="px-2 py-3 text-center text-gray-400 dark:text-gray-500">
                  {t("common:loading")}
                </td>
              </tr>
            ) : page.rows.length === 0 ? (
              <tr>
                <td colSpan={TOTAL_COLUMN_COUNT} className="px-2 py-3 text-center text-gray-400 dark:text-gray-500">
                  {t("llmUsageNoRows")}
                </td>
              </tr>
            ) : (
              page.rows.map((row) => <OperationRow key={row.operationId} row={row} basePath={basePath} />)
            )}
          </tbody>
        </table>
      </div>
      <PaginationBar page={pageNumber} pageSize={pageSize} total={page.total} onPageChange={onPageChange} />
    </div>
  );
}

/// The aggregate row over the WHOLE filtered set, not the page on screen - `page.totals` comes straight
/// from the API's own aggregate query (see `LlmUsageTotals`'s doc comment), so it stays correct the moment
/// there is a second page. Never compute this by summing `page.rows`.
function TotalsRow({ totals }: { totals: LlmUsageTotals }) {
  const { t } = useTranslation("account");
  return (
    <tr
      data-testid="llm-usage-totals-row"
      className="sticky top-[26px] z-10 border-b bg-gray-50 font-medium text-gray-700 dark:border-gray-800 dark:bg-gray-800 dark:text-gray-200"
    >
      <td colSpan={6} className="px-2 py-1">
        {t("llmUsageTotalsLabel", { calls: totals.calls, operations: totals.operations })}
      </td>
      <td className="px-2 py-1 text-right">{formatDuration(totals.durationMs)}</td>
      {/* Each cell uses that column's OWN measured count (promptTokensMeasured, etc.), never the coarser
          any-column tokenMeasuredCalls - see LlmUsageTotals's doc comment for why reusing one shared
          figure across four independently-nullable columns states something false about at least three
          of them whenever they differ (which, for reasoning tokens especially, is the common case). */}
      <TokenTotalCell value={totals.promptTokens} measured={totals.promptTokensMeasured} total={totals.calls} />
      <TokenTotalCell value={totals.completionTokens} measured={totals.completionTokensMeasured} total={totals.calls} />
      <TokenTotalCell value={totals.reasoningTokens} measured={totals.reasoningTokensMeasured} total={totals.calls} />
      <TokenTotalCell value={totals.totalTokens} measured={totals.totalTokensMeasured} total={totals.calls} />
      <td className="px-2 py-1">{t("llmUsageTotalsFailed", { count: totals.failedCalls })}</td>
    </tr>
  );
}

/// A token-sum cell for the totals row. `value` is `number | null` - null means nothing in the filtered set
/// reported this field, which must never render as "0". `measured` must be THIS column's own measured
/// count (`promptTokensMeasured`, etc.) - never the coarser any-column `tokenMeasuredCalls`, which would
/// misstate how trustworthy this specific total is whenever the columns' real measured counts differ (see
/// the call site in `TotalsRow`). A total that silently covers only part of the set, without saying so,
/// is worse than no total.
function TokenTotalCell({ value, measured, total }: { value: number | null; measured: number; total: number }) {
  const { t } = useTranslation("account");
  return (
    <td className="px-2 py-1 text-right">
      <div>{value === null ? t("llmUsageNotMeasured") : value.toLocaleString()}</div>
      {/* Only shown when there IS a total to qualify - a bare dash already says "nothing measured", so a
          "measured on N of M calls" line next to it would contradict itself. */}
      {value !== null && (
        <div className="text-[10px] font-normal text-gray-400 dark:text-gray-500">
          {t("llmUsageMeasuredOn", { measured, total })}
        </div>
      )}
    </td>
  );
}

function OperationRow({ row, basePath }: { row: LlmUsageOperationRow; basePath: string }) {
  const { t, i18n } = useTranslation("account");
  return (
    <tr className="border-b hover:bg-gray-50 dark:border-gray-800 dark:hover:bg-gray-800/60">
      <td className="whitespace-nowrap px-2 py-1">{new Date(row.startedAt).toLocaleString(i18n.language)}</td>
      <td className="px-2 py-1">{kindLabel(t, row.kind)}</td>
      <td className="max-w-[12rem] truncate px-2 py-1">{row.userEmail}</td>
      <RecordingCell row={row} basePath={basePath} />
      <td className="max-w-[10rem] truncate px-2 py-1">{row.model}</td>
      <td className="px-2 py-1 text-right">{row.turns}</td>
      <td className="px-2 py-1 text-right">
        {formatDuration(new Date(row.completedAt).getTime() - new Date(row.startedAt).getTime())}
      </td>
      <TokenCell value={row.promptTokens} />
      <TokenCell value={row.completionTokens} />
      <TokenCell value={row.reasoningTokens} />
      <TokenCell value={row.totalTokens} />
      <OutcomeCell success={row.success} />
    </tr>
  );
}

/// A single call's own token field. Unlike the totals row this needs no "measured on N of M" qualifier -
/// there's nothing to average, just one call that either reported a number or didn't. Null still must never
/// render as "0" (the same measured-vs-zero distinction the totals row preserves).
function TokenCell({ value }: { value: number | null }) {
  const { t } = useTranslation("account");
  return (
    <td className="px-2 py-1 text-right">
      {value === null ? <span className="text-gray-400 dark:text-gray-500">{t("llmUsageNotMeasured")}</span> : value.toLocaleString()}
    </td>
  );
}

/// Links to the recording and/or folder an operation belongs to, built through `useRoomBasePath` (passed
/// down as `basePath`) so they land in the room the recording actually lives in rather than always falling
/// back to the viewer's Personal room. Rendered unconditionally whenever an id is present - a Platform
/// Administrator has no automatic access to another user's recording, so some of these will 403, but
/// pre-checking access per row would cost far more than the occasional dead link saves.
function RecordingCell({ row, basePath }: { row: LlmUsageOperationRow; basePath: string }) {
  const { t } = useTranslation("account");
  if (!row.recordingId && !row.sectionId) {
    return <td className="px-2 py-1 text-gray-400 dark:text-gray-500">{t("llmUsageNoRecording")}</td>;
  }
  return (
    <td className="max-w-[14rem] px-2 py-1">
      {row.recordingId && (
        <Link
          to={`${basePath}/recordings/${row.recordingId}`}
          className="block truncate text-indigo-600 hover:underline dark:text-indigo-400"
        >
          {row.recordingTitle ?? row.recordingId}
        </Link>
      )}
      {row.sectionId && (
        <Link
          to={`${basePath}/sections/${row.sectionId}`}
          className="block truncate text-[10px] text-gray-500 hover:underline dark:text-gray-400"
        >
          {row.sectionName ?? row.sectionId}
        </Link>
      )}
    </td>
  );
}

function OutcomeCell({ success }: { success: boolean }) {
  const { t } = useTranslation("account");
  return (
    <td className="px-2 py-1">
      <span
        className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${
          success
            ? "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300"
            : "bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-300"
        }`}
      >
        {success ? t("llmUsageOutcomeSuccess") : t("llmUsageOutcomeFailure")}
      </span>
    </td>
  );
}

function PaginationBar({
  page,
  pageSize,
  total,
  onPageChange,
}: {
  page: number;
  pageSize: number;
  total: number;
  onPageChange: (page: number) => void;
}) {
  const { t } = useTranslation("account");
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, total);
  const hasPrev = page > 1;
  const hasNext = page * pageSize < total;
  return (
    <div className="flex items-center justify-end gap-2 border-t px-3 py-1.5 text-xs text-gray-500 dark:border-gray-800 dark:text-gray-400">
      <span>{t("llmUsagePageRange", { from, to, total })}</span>
      <button
        type="button"
        disabled={!hasPrev}
        onClick={() => onPageChange(page - 1)}
        className="rounded border px-2 py-0.5 disabled:opacity-40 dark:border-gray-700"
      >
        {t("llmUsagePagePrev")}
      </button>
      <button
        type="button"
        disabled={!hasNext}
        onClick={() => onPageChange(page + 1)}
        className="rounded border px-2 py-0.5 disabled:opacity-40 dark:border-gray-700"
      >
        {t("llmUsagePageNext")}
      </button>
    </div>
  );
}
