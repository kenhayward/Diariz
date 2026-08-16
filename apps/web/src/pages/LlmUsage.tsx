import { useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { useAuth } from "../auth";
import { api } from "../lib/api";
import { useRoomBasePath } from "../lib/rooms";
import type { LlmUsageOperationRow, LlmUsagePage, LlmUsageSortKey } from "../lib/types";
import UsageFilterBar, { defaultUsageFilter, type UsageFilterState } from "../components/llmusage/UsageFilterBar";
import UsageTable from "../components/llmusage/UsageTable";

const PAGE_SIZE = 50;

/// Platform-Administrator-only viewer over the LLM usage log, reached at /admin/llm-usage behind the app
/// login (see App.tsx). `RequireAuth` there only checks that someone is signed in, not what they're allowed
/// to see, so the permission gate lives here: a non-admin gets a short refusal instead of the table.
///
/// `mode` is fixed to "operations" (one row per operation - every LlmCalls row sharing an operationId,
/// collapsed) for this task; a calls-level drill-down and the roll-up summary view are Task 8's job, which
/// is why this file, the filter bar, and the table stay separate rather than one page owning everything.
export default function LlmUsage() {
  const { t } = useTranslation("account");
  const { isPlatformAdmin } = useAuth();
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

  const [filter, setFilter] = useState<UsageFilterState>(() => defaultUsageFilter());
  const [sort, setSort] = useState<LlmUsageSortKey>("startedAt");
  const [desc, setDesc] = useState(true);
  const [page, setPage] = useState(1);

  // Every hook below must run unconditionally on every render - including for a non-admin - so the refusal
  // below is a plain early RETURN after all hooks, never a conditional hook call.
  const filterOptionsQuery = useQuery({
    queryKey: ["llm-usage-filters", filter.from, filter.to],
    queryFn: () => api.getLlmUsageFilters({ from: filter.from, to: filter.to }),
    enabled: isPlatformAdmin,
  });

  const usageQuery = useQuery({
    queryKey: ["llm-usage", filter, sort, desc, page],
    queryFn: async () =>
      (await api.getLlmUsage({
        mode: "operations",
        from: filter.from,
        to: filter.to,
        userIds: filter.userIds.length ? filter.userIds : undefined,
        kinds: filter.kinds.length ? filter.kinds : undefined,
        models: filter.models.length ? filter.models : undefined,
        outcome: filter.outcome,
        sort,
        desc,
        page,
        pageSize: PAGE_SIZE,
      })) as LlmUsagePage<LlmUsageOperationRow>,
    enabled: isPlatformAdmin,
    // Keep the previous page's rows on screen while a new page/sort/filter loads instead of flashing to a
    // loading state - the totals row and pager both read off the same query, so this keeps them in sync too.
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

  if (!isPlatformAdmin) {
    return (
      <div className="flex h-screen flex-col">
        <TopBar />
        <p className="p-6 text-sm text-gray-600 dark:text-gray-300">{t("llmUsageForbidden")}</p>
      </div>
    );
  }

  return (
    <div className="flex h-screen flex-col">
      <TopBar />
      {/* GET /filters failing is otherwise silent - the multi-selects would just render with no options
          and nothing would say why, unlike the table's own explicit error state below. */}
      {filterOptionsQuery.isError && (
        <p className="border-b bg-amber-50 px-3 py-1 text-xs text-amber-800 dark:border-gray-800 dark:bg-amber-900/20 dark:text-amber-300">
          {t("llmUsageFiltersLoadError")}
        </p>
      )}
      <UsageFilterBar filter={filter} onChange={updateFilter} filterOptions={filterOptionsQuery.data} />
      <div className="min-h-0 flex-1">
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
      </div>
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
