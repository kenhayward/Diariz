import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import type { LlmUsageGroupDimension, LlmUsageSummary, LlmUsageSummaryGroup, LlmUsageTotals } from "../../lib/types";
import { kindLabel } from "./UsageFilterBar";

/// Canonical left-to-right order for the group dimensions - fixed regardless of click order, so a chip
/// toggle always produces the same `groupBy` array shape (e.g. clicking Model then Kind, or Kind then
/// Model, both end up ["model", "kind"]). Matches `LlmUsageGroupDimension`.
const GROUP_DIMENSIONS: LlmUsageGroupDimension[] = ["user", "model", "kind"];

/// Numeric columns the roll-up table may be sorted by. Sorting is entirely client-side (see the module
/// doc below) - there is no server `sort` parameter for the summary endpoint, and the server caps the
/// number of groups it returns, so re-ordering the already-fetched set in the browser is cheap and safe.
type SummarySortKey =
  | "calls"
  | "operations"
  | "averageTurnsPerOperation"
  | "maxTurnsPerOperation"
  | "promptTokens"
  | "completionTokens"
  | "reasoningTokens"
  | "totalTokens"
  | "tokensPerSecond"
  | "failedCalls";

function dimLabelKey(dim: LlmUsageGroupDimension): string {
  return dim === "user" ? "llmUsageColUser" : dim === "model" ? "llmUsageColModel" : "llmUsageColKind";
}

/// The roll-up ("Summary" mode) view over the LLM usage log: a chip toggle for which dimensions
/// (user/model/kind) to group by, a client-sorted results table, and a totals row over the WHOLE filtered
/// set (never a fold over the visible groups - see `SummaryTotalsRow`). `groupBy` is owned by the parent
/// page (it drives the `GET /summary` query), so a chip click here only ever calls `onGroupByChange` -
/// this component holds no query state itself, only its own local sort state (which never leaves the
/// browser, so there is nothing for the parent to own).
///
/// `averageTurnsPerOperation`/`maxTurnsPerOperation` are rendered exactly as the API reports them, never
/// multiplied or added across groups - "how many turns did operations in this group typically take" has no
/// meaningful sum, only an average and a maximum, which is exactly what the server already computed.
export default function UsageSummary({
  summary,
  isLoading,
  isError,
  groupBy,
  onGroupByChange,
}: {
  summary: LlmUsageSummary | undefined;
  isLoading: boolean;
  isError: boolean;
  groupBy: LlmUsageGroupDimension[];
  onGroupByChange: (next: LlmUsageGroupDimension[]) => void;
}) {
  const { t } = useTranslation("account");
  // `null` means "no column picked yet" - the groups render in whatever order the API returned them, same
  // as UsageTable's rows do before any header is clicked. Picking any column for the first time starts
  // ascending (matching UsageTable's own "a newly-picked column starts ascending" convention); clicking the
  // SAME column again toggles direction.
  const [sortKey, setSortKey] = useState<SummarySortKey | null>(null);
  const [desc, setDesc] = useState(false);

  const dims = GROUP_DIMENSIONS.filter((d) => groupBy.includes(d));

  const sortedGroups = useMemo(() => {
    const groups = summary ? [...summary.groups] : [];
    if (sortKey === null) return groups;
    groups.sort((a, b) => {
      const av = a[sortKey];
      const bv = b[sortKey];
      // Nulls sort last regardless of direction - a "not measured" column has no rank, so it never gets to
      // masquerade as the smallest (or, on a descending sort, the largest) real value.
      if (av === null && bv === null) return 0;
      if (av === null) return 1;
      if (bv === null) return -1;
      const diff = av - bv;
      return desc ? -diff : diff;
    });
    return groups;
  }, [summary, sortKey, desc]);

  function toggleDim(dim: LlmUsageGroupDimension) {
    const selected = groupBy.includes(dim);
    // The server 400s on an empty groupBy - refuse to let the last remaining dimension be turned off.
    if (selected && groupBy.length === 1) return;
    const nextSet = selected ? groupBy.filter((d) => d !== dim) : [...groupBy, dim];
    onGroupByChange(GROUP_DIMENSIONS.filter((d) => nextSet.includes(d)));
  }

  function toggleSort(key: SummarySortKey) {
    if (key === sortKey) {
      setDesc((d) => !d);
    } else {
      setSortKey(key);
      setDesc(false);
    }
  }

  function sortHeader(key: SummarySortKey, labelKey: string) {
    const active = sortKey === key;
    return (
      <th className="px-2 py-1 text-right font-medium text-gray-500 dark:text-gray-400" role="columnheader" aria-label={t(labelKey)}>
        <button
          type="button"
          data-testid={`llm-usage-summary-sort-${key}`}
          onClick={() => toggleSort(key)}
          className="flex w-full items-center justify-end gap-1 hover:text-gray-800 dark:hover:text-gray-100"
        >
          {t(labelKey)}
          {active && <span aria-hidden="true">{desc ? "▼" : "▲"}</span>}
        </button>
      </th>
    );
  }

  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b px-3 py-2 dark:border-gray-800">
        <span className="text-xs text-gray-500 dark:text-gray-400">{t("llmUsageGroupByLabel")}</span>
        {GROUP_DIMENSIONS.map((dim) => {
          const active = groupBy.includes(dim);
          return (
            <button
              type="button"
              key={dim}
              aria-pressed={active}
              onClick={() => toggleDim(dim)}
              className={`rounded-full border px-2.5 py-1 text-xs font-medium ${
                active
                  ? "border-blue-300 bg-blue-50 text-blue-700 dark:border-blue-800 dark:bg-blue-900/40 dark:text-blue-300"
                  : "border-gray-200 text-gray-600 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
              }`}
            >
              {t(dimLabelKey(dim))}
            </button>
          );
        })}
      </div>

      {isError ? (
        <p className="p-4 text-sm text-red-600 dark:text-red-400">{t("llmUsageSummaryLoadError")}</p>
      ) : !summary ? (
        <p className="p-4 text-sm text-gray-400 dark:text-gray-500">{t("common:loading")}</p>
      ) : (
        <div className="min-h-0 flex-1 overflow-auto">
          <table className="w-full min-w-[900px] table-auto border-collapse text-xs">
            <thead>
              <tr className="sticky top-0 z-20 border-b bg-white dark:border-gray-800 dark:bg-gray-900">
                {dims.map((dim) => (
                  <th key={dim} className="px-2 py-1 text-left font-medium text-gray-500 dark:text-gray-400" role="columnheader">
                    {t(dimLabelKey(dim))}
                  </th>
                ))}
                {sortHeader("calls", "llmUsageColCalls")}
                {sortHeader("operations", "llmUsageColOperations")}
                {sortHeader("averageTurnsPerOperation", "llmUsageColAvgTurns")}
                {sortHeader("maxTurnsPerOperation", "llmUsageColMaxTurns")}
                {sortHeader("promptTokens", "llmUsageColPromptTokens")}
                {sortHeader("completionTokens", "llmUsageColCompletionTokens")}
                {sortHeader("reasoningTokens", "llmUsageColReasoningTokens")}
                {sortHeader("totalTokens", "llmUsageColTotalTokens")}
                {sortHeader("tokensPerSecond", "llmUsageColTokensPerSec")}
                {sortHeader("failedCalls", "llmUsageColFailed")}
              </tr>
            </thead>
            <tbody>
              {isLoading ? (
                <tr>
                  <td colSpan={dims.length + 10} className="px-2 py-3 text-center text-gray-400 dark:text-gray-500">
                    {t("common:loading")}
                  </td>
                </tr>
              ) : sortedGroups.length === 0 ? (
                <tr>
                  <td colSpan={dims.length + 10} className="px-2 py-3 text-center text-gray-400 dark:text-gray-500">
                    {t("llmUsageNoRows")}
                  </td>
                </tr>
              ) : (
                sortedGroups.map((group, i) => <SummaryRow key={i} group={group} dims={dims} testId={`llm-usage-summary-row-${i}`} />)
              )}
            </tbody>
            {/* Pinned to the BOTTOM of the scroll container, not offset from the header - see
                UsageTable.tsx's identical `TotalsRow`/`tfoot` for why: a pixel offset guessed from the
                header's rendered height drifts the moment font size, padding, line height, or
                locale-driven text wrapping changes it (verified live in the browser to actually happen -
                see task-8-report.md's fix-round-2 section). A <tfoot> row pins to the bottom of its
                scrolling ancestor with `position: sticky; bottom: 0` regardless of header height. */}
            <SummaryTotalsRow totals={summary.totals} colSpan={dims.length} />
          </table>
        </div>
      )}
    </div>
  );
}

/// The aggregate row over the WHOLE filtered set (`summary.totals`), never a fold over the visible groups.
/// Like `UsageTable`'s own totals row, each token column is captioned with its OWN measured count
/// (`promptTokensMeasured`, etc. - `LlmUsageTotals` carries these) - unlike a group row, which only has the
/// coarser any-column `tokenMeasuredCalls` (there is no finer-grained figure per group on the wire).
///
/// Rendered as a `<tfoot>`, sticky to the BOTTOM of the scrolling container (`bottom-0`) - deliberately NOT
/// offset from the header by a pixel figure. An earlier version pinned it under `<thead>` with a hand-typed
/// `top-[26px]`, which looked right in isolation but was verified live in the browser to be wrong: the real
/// rendered header is about 40px tall, so roughly 14px (~22%) of the totals row sat hidden underneath it
/// once the table was actually scrolled - see task-8-report.md's fix-round-2 section for the measured
/// numbers (the identical bug, found and fixed the same way, one file over in `UsageTable.tsx`). A
/// `<tfoot>` pinned to the bottom has no dependency on the header's height at all, so it cannot drift the
/// same way if the header's font size, padding, line height, or locale-driven text wrapping ever changes -
/// and totals beneath the data is the more conventional reading order for a table besides.
function SummaryTotalsRow({ totals, colSpan }: { totals: LlmUsageTotals; colSpan: number }) {
  const { t } = useTranslation("account");
  return (
    <tfoot>
      <tr
        data-testid="llm-usage-summary-totals-row"
        className="sticky bottom-0 z-10 border-t bg-gray-50 font-medium text-gray-700 dark:border-gray-800 dark:bg-gray-800 dark:text-gray-200"
      >
        <td colSpan={colSpan + 2} className="px-2 py-1">
          {t("llmUsageTotalsLabel", { calls: totals.calls, operations: totals.operations })}
        </td>
        <td className="px-2 py-1 text-right" colSpan={2} />
        <TotalCell value={totals.promptTokens} measured={totals.promptTokensMeasured} total={totals.calls} />
        <TotalCell value={totals.completionTokens} measured={totals.completionTokensMeasured} total={totals.calls} />
        <TotalCell value={totals.reasoningTokens} measured={totals.reasoningTokensMeasured} total={totals.calls} />
        <TotalCell value={totals.totalTokens} measured={totals.totalTokensMeasured} total={totals.calls} />
        <td className="px-2 py-1 text-right">
          {totals.tokensPerSecond === null ? t("llmUsageNotMeasured") : totals.tokensPerSecond.toFixed(1)}
        </td>
        <td className="px-2 py-1 text-right">{t("llmUsageTotalsFailed", { count: totals.failedCalls })}</td>
      </tr>
    </tfoot>
  );
}

function TotalCell({ value, measured, total }: { value: number | null; measured: number; total: number }) {
  const { t } = useTranslation("account");
  return (
    <td className="px-2 py-1 text-right">
      <div>{value === null ? t("llmUsageNotMeasured") : value.toLocaleString()}</div>
      {value !== null && (
        <div className="text-[10px] font-normal text-gray-400 dark:text-gray-500">{t("llmUsageMeasuredOn", { measured, total })}</div>
      )}
    </td>
  );
}

/// One roll-up row. `tokenMeasuredCalls` is the only measured-count `LlmUsageSummaryGroup` carries (there
/// is no per-column breakdown at group granularity), so every token cell here is captioned with that same
/// coarser figure - unlike the totals row above, this is not a misuse of a coarser number in place of a
/// finer one, it is the only number the API gives at this level.
function SummaryRow({ group, dims, testId }: { group: LlmUsageSummaryGroup; dims: LlmUsageGroupDimension[]; testId: string }) {
  const { t } = useTranslation("account");
  return (
    <tr data-testid={testId} className="border-b hover:bg-gray-50 dark:border-gray-800 dark:hover:bg-gray-800/60">
      {dims.map((dim) => (
        <td key={dim} className="max-w-[12rem] truncate px-2 py-1">
          {dim === "user" ? (group.userEmail ?? "") : dim === "model" ? (group.model ?? "") : group.kind ? kindLabel(t, group.kind) : ""}
        </td>
      ))}
      <td className="px-2 py-1 text-right">{group.calls.toLocaleString()}</td>
      <td className="px-2 py-1 text-right">{group.operations.toLocaleString()}</td>
      <td className="px-2 py-1 text-right">{group.averageTurnsPerOperation.toFixed(1)}</td>
      <td className="px-2 py-1 text-right">{group.maxTurnsPerOperation}</td>
      <GroupTokenCell value={group.promptTokens} measured={group.tokenMeasuredCalls} total={group.calls} />
      <GroupTokenCell value={group.completionTokens} measured={group.tokenMeasuredCalls} total={group.calls} />
      <GroupTokenCell value={group.reasoningTokens} measured={group.tokenMeasuredCalls} total={group.calls} />
      <GroupTokenCell value={group.totalTokens} measured={group.tokenMeasuredCalls} total={group.calls} />
      <td className="px-2 py-1 text-right">{group.tokensPerSecond === null ? t("llmUsageNotMeasured") : group.tokensPerSecond.toFixed(1)}</td>
      <td className="px-2 py-1 text-right">{group.failedCalls.toLocaleString()}</td>
    </tr>
  );
}

function GroupTokenCell({ value, measured, total }: { value: number | null; measured: number; total: number }) {
  const { t } = useTranslation("account");
  return (
    <td className="px-2 py-1 text-right">
      <div>{value === null ? t("llmUsageNotMeasured") : value.toLocaleString()}</div>
      {value !== null && (
        <div className="text-[10px] font-normal text-gray-400 dark:text-gray-500">{t("llmUsageMeasuredOn", { measured, total })}</div>
      )}
    </td>
  );
}
