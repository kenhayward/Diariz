import { useState } from "react";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link, useSearchParams } from "react-router-dom";
import { useAuth } from "../auth";
import { api, apiErrorMessage } from "../lib/api";
import { useRoomBasePath } from "../lib/rooms";
import type { LlmUsageFilter, LlmUsageGroupDimension, LlmUsageSortKey } from "../lib/types";
import UsageFilterBar, { type UsageFilterState } from "../components/llmusage/UsageFilterBar";
import { usageFilterFromParams } from "../components/llmusage/usageFilterParams";
import UsageTable from "../components/llmusage/UsageTable";
import UsageSummary from "../components/llmusage/UsageSummary";

const PAGE_SIZE = 50;

type ViewMode = "operations" | "calls" | "summary";

/// The wire filter shared by every /api/admin/llm-usage endpoint, built the SAME way for the list query,
/// the summary query, AND the delete call - a single source of truth so "delete" can never drift from
/// "what's currently on screen" (see `handleDelete` below, the whole point of this task's filtered-delete
/// requirement).
function toApiFilter(filter: UsageFilterState): LlmUsageFilter {
  return {
    from: filter.from,
    to: filter.to,
    userIds: filter.userIds.length ? filter.userIds : undefined,
    kinds: filter.kinds.length ? filter.kinds : undefined,
    models: filter.models.length ? filter.models : undefined,
    outcome: filter.outcome,
  };
}

/// Platform-Administrator-only viewer over the LLM usage log, reached at /admin/llm-usage behind the app
/// login (see App.tsx). `RequireAuth` there only checks that someone is signed in, not what they're allowed
/// to see, so the permission gate lives here: a non-admin gets a short refusal instead of the table.
interface Props {
  /// Rendered inside the settings modal rather than as its own route: drops the top bar and the
  /// full-height shell, which the host provides. The route still exists for a pasted or bookmarked link.
  embedded?: boolean;
  /// Seeds the filter when there is no URL to read one from - the settings modal has no route of its own,
  /// so a "show me these calls" request arrives as a query string rather than a navigation.
  initialQuery?: string;
}

export default function LlmUsage({ embedded = false, initialQuery }: Props = {}) {
  const { t } = useTranslation("account");
  const { isPlatformAdmin } = useAuth();
  const queryClient = useQueryClient();
  // Outside any RoomProvider (this is a top-level route, not nested under WorkspaceLayout), so this
  // resolves to "" - the top-level route prefix. Still routed through the hook rather than hard-coded, so a
  // recording/folder link is built the same way every other link in the app is.
  //
  // FORWARD-LOOKING GAP: LlmUsageOperationRow carries no per-row room id, so this single page-wide
  // basePath cannot be room-correct for a recording that lives in a SHARED room once those exist for real
  // (today there is effectively one room per user, so this is unobservable). Once cross-room usage rows
  // are possible, a link built from this basePath would open the wrong room the same way the bugs
  // documented in "Room-aware links and routes" (this repo's own memory notes, PRs #298/#299) did -
  // fixing it needs the API to start returning a room id per row, not a client-side workaround here.
  const basePath = useRoomBasePath();

  // Hydrated ONCE from the query string, then owned by the page. The URL is a way IN - from the model
  // editor's "Open in usage log", or a pasted link - not a mirror of the filter bar: keeping the two in
  // sync would push a history entry on every checkbox and make Back mean something the user did not do.
  const [searchParams] = useSearchParams();
  const [filter, setFilter] = useState<UsageFilterState>(() =>
    usageFilterFromParams(initialQuery !== undefined ? new URLSearchParams(initialQuery) : searchParams),
  );
  const [sort, setSort] = useState<LlmUsageSortKey>("startedAt");
  const [desc, setDesc] = useState(true);
  const [page, setPage] = useState(1);
  const [mode, setMode] = useState<ViewMode>("operations");
  const [groupBy, setGroupBy] = useState<LlmUsageGroupDimension[]>(["kind"]);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  // Every hook below must run unconditionally on every render - including for a non-admin - so the refusal
  // below is a plain early RETURN after all hooks, never a conditional hook call.
  const filterOptionsQuery = useQuery({
    queryKey: ["llm-usage-filters", filter.from, filter.to],
    queryFn: () => api.getLlmUsageFilters({ from: filter.from, to: filter.to }),
    enabled: isPlatformAdmin,
  });

  const listMode = mode === "calls" ? "calls" : "operations";
  const usageQuery = useQuery({
    queryKey: ["llm-usage", filter, sort, desc, page, listMode],
    queryFn: () =>
      api.getLlmUsage({
        mode: listMode,
        ...toApiFilter(filter),
        sort,
        desc,
        page,
        pageSize: PAGE_SIZE,
      }),
    enabled: isPlatformAdmin && mode !== "summary",
    // Keep the previous page's rows on screen while a new page/sort/filter loads instead of flashing to a
    // loading state - the totals row and pager both read off the same query, so this keeps them in sync too.
    placeholderData: keepPreviousData,
  });

  const summaryQuery = useQuery({
    queryKey: ["llm-usage-summary", filter, groupBy],
    queryFn: () => api.getLlmUsageSummary({ ...toApiFilter(filter), groupBy }),
    enabled: isPlatformAdmin && mode === "summary",
    placeholderData: keepPreviousData,
  });

  function updateFilter(next: UsageFilterState) {
    setFilter(next);
    setPage(1); // a changed filter invalidates the current page position
  }

  function toggleSort(key: LlmUsageSortKey) {
    if (key === sort) {
      setDesc((d) => !d);
    } else {
      setSort(key);
      setDesc(false);
    }
    setPage(1);
  }

  function changeMode(next: ViewMode) {
    setMode(next);
    setPage(1);
  }

  // The count a delete would remove, and the number stated in the confirm dialog below, is ALWAYS
  // `totals.calls` - the aggregate over the WHOLE filtered set of LlmCalls rows (what a delete actually
  // removes), never `usageQuery.data.total` (which in operations mode counts OPERATIONS, not calls) and
  // never the number of rows rendered on the current page. `totals.calls` is present on both the list
  // page's totals and the summary's totals, so this one figure stays correct across all three modes.
  const activeTotals = mode === "summary" ? summaryQuery.data?.totals : usageQuery.data?.totals;
  const deleteCount = activeTotals?.calls;
  // Both queries use `placeholderData: keepPreviousData`, so `data` (and therefore `deleteCount` above) can
  // still belong to the PREVIOUS filter while a refetch for a newly-changed filter is in flight -
  // `isFetching` is what actually tells the two apart (`isLoading` is only true on the very first fetch).
  // Gating on it closes the exact race the filtered-delete requirement calls the worst possible outcome: a
  // stale, smaller count staying on screen (and reachable) while the delete itself would run against the
  // NEW, already-live filter state.
  const activeIsFetching = mode === "summary" ? summaryQuery.isFetching : usageQuery.isFetching;
  const deleteBlocked = deleteCount === undefined || deleteCount === 0 || deleting || activeIsFetching;

  async function handleDelete() {
    // Re-checked here too (not just via the button's `disabled`) so there is no path - keyboard, a
    // programmatic click, a future second call site - that can reach the API call while the count on
    // screen might not match the filter the delete would actually run against.
    if (deleteBlocked) return;
    if (!window.confirm(t("llmUsageDeleteConfirm", { count: deleteCount }))) return;
    setDeleteError(null);
    setDeleting(true);
    try {
      // The EXACT current filter, built by the same `toApiFilter` the queries above use - a delete that
      // silently dropped the filter would destroy far more than the administrator asked for.
      await api.deleteLlmUsage(toApiFilter(filter));
      setPage(1); // the current page may no longer exist once the matching rows are gone
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["llm-usage"] }),
        queryClient.invalidateQueries({ queryKey: ["llm-usage-summary"] }),
        queryClient.invalidateQueries({ queryKey: ["llm-usage-filters"] }),
      ]);
    } catch (e) {
      setDeleteError(apiErrorMessage(e, t("llmUsageDeleteError")));
    } finally {
      setDeleting(false);
    }
  }

  if (!isPlatformAdmin) {
    return (
      <Shell embedded={embedded}>
        <p className="p-6 text-sm text-gray-600 dark:text-gray-300">{t("llmUsageForbidden")}</p>
      </Shell>
    );
  }

  return (
    <Shell embedded={embedded}>
      {/* GET /filters failing is otherwise silent - the multi-selects would just render with no options
          and nothing would say why, unlike the table's own explicit error state below. */}
      {filterOptionsQuery.isError && (
        <p className="border-b bg-amber-50 px-3 py-1 text-xs text-amber-800 dark:border-gray-800 dark:bg-amber-900/20 dark:text-amber-300">
          {t("llmUsageFiltersLoadError")}
        </p>
      )}
      <div className="flex items-center justify-between border-b px-3 py-2 dark:border-gray-800">
        <ModeTabs mode={mode} onChange={changeMode} />
        {/* The destructive action, visually distinct (red) from every other control on this bar. */}
        <button
          type="button"
          onClick={() => void handleDelete()}
          disabled={deleteBlocked}
          className="rounded-full border border-red-300 bg-red-50 px-3 py-1 text-xs font-semibold text-red-700 hover:bg-red-100 disabled:opacity-40 dark:border-red-800 dark:bg-red-900/30 dark:text-red-300 dark:hover:bg-red-900/50"
        >
          {deleting ? t("llmUsageDeleting") : t("llmUsageDeleteButton")}
        </button>
      </div>
      {deleteError && (
        <p className="border-b bg-red-50 px-3 py-1 text-xs text-red-800 dark:border-gray-800 dark:bg-red-900/20 dark:text-red-300">
          {deleteError}
        </p>
      )}
      <UsageFilterBar filter={filter} onChange={updateFilter} filterOptions={filterOptionsQuery.data} />
      <div className="min-h-0 flex-1">
        {mode === "summary" ? (
          <UsageSummary
            summary={summaryQuery.data}
            isLoading={summaryQuery.isLoading}
            isError={summaryQuery.isError}
            groupBy={groupBy}
            onGroupByChange={setGroupBy}
          />
        ) : (
          <UsageTable
            page={usageQuery.data}
            isLoading={usageQuery.isLoading}
            isError={usageQuery.isError}
            sort={sort}
            desc={desc}
            onSort={toggleSort}
            pageNumber={page}
            pageSize={PAGE_SIZE}
            onPageChange={setPage}
            basePath={basePath}
          />
        )}
      </div>
    </Shell>
  );
}

/// The page chrome, or none of it when the panel is hosted in a modal that already provides its own.
function Shell({ embedded, children }: { embedded: boolean; children: React.ReactNode }) {
  if (embedded) return <div className="flex min-h-0 flex-1 flex-col">{children}</div>;
  return (
    <div className="flex h-screen flex-col">
      <TopBar />
      {children}
    </div>
  );
}

function ModeTabs({ mode, onChange }: { mode: ViewMode; onChange: (m: ViewMode) => void }) {
  const { t } = useTranslation("account");
  function tab(value: ViewMode, labelKey: string) {
    const active = mode === value;
    return (
      <button
        type="button"
        key={value}
        aria-pressed={active}
        onClick={() => onChange(value)}
        className={`rounded-full border px-3 py-1 text-xs font-medium ${
          active
            ? "border-blue-300 bg-blue-50 text-blue-700 dark:border-blue-800 dark:bg-blue-900/40 dark:text-blue-300"
            : "border-gray-200 text-gray-600 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
        }`}
      >
        {t(labelKey)}
      </button>
    );
  }
  return (
    <div className="flex items-center gap-1">
      {tab("operations", "llmUsageModeOperations")}
      {tab("calls", "llmUsageModeCalls")}
      {tab("summary", "llmUsageModeSummary")}
    </div>
  );
}

function TopBar() {
  const { t } = useTranslation("account");
  return (
    <div className="flex items-center gap-3 border-b bg-white px-4 py-2 text-sm dark:border-gray-700 dark:bg-gray-900">
      <Link to="/" className="text-indigo-600 hover:underline dark:text-indigo-400">
        ← {t("apiBackToApp")}
      </Link>
      <span className="font-medium text-gray-700 dark:text-gray-200">{t("llmUsageViewerTitle")}</span>
    </div>
  );
}
