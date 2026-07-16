import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { api } from "../../lib/api";
import { useRoomBasePath } from "../../lib/rooms";
import { useDrillSearch } from "../../lib/drillRoute";
import { sectionColor } from "../../lib/sectionColors";
import { formatDuration } from "../../lib/format";
import { highlight, snippetWindow } from "../../lib/highlight";
import { facetsOf, applyFilters, groupBySection, UNGROUPED_GROUP, type SearchFilterState } from "../../lib/searchResults";
import SearchFilters from "./SearchFilters";
import { SearchIcon, FolderIcon, ChevronRightIcon, GlobeIcon } from "../icons";
import type { RecordingSearchHit } from "../../lib/types";

/// Debounce before asking the server. Long enough that typing a word is one request, short enough that the
/// results feel like they're keeping up.
const DEBOUNCE_MS = 250;

/// The nav's always-present search field, plus the results that take over the list body while a query is
/// live. It owns the query as component state and reports it upward; the drill position stays in the URL, so
/// clearing the query restores exactly where the user was browsing without any save/restore of its own.
export default function SearchBar({
  roomId,
  sectionId,
  scopeName,
  onQueryChange,
  onDrill,
}: {
  roomId: string | undefined;
  /// The folder being browsed - the default search scope. Null at the room's top level.
  sectionId: string | null;
  /// What to call the current scope on the chip.
  scopeName: string;
  /// Fired whenever the live query changes, so the panel can swap its list body for these results.
  onQueryChange: (query: string) => void;
  onDrill?: (sectionId: string) => void;
}) {
  const { t } = useTranslation("workspace");
  const [value, setValue] = useState("");
  const [debounced, setDebounced] = useState("");

  useEffect(() => {
    const id = setTimeout(() => setDebounced(value.trim()), DEBOUNCE_MS);
    return () => clearTimeout(id);
  }, [value]);

  // Scope + filters are per-search: clearing the box is the way back to the drill, so neither may outlive
  // the search that set them, or the next query would silently inherit a scope nobody asked for.
  const [everywhere, setEverywhere] = useState(false);
  const [filters, setFilters] = useState<SearchFilterState>({});

  function change(next: string) {
    setValue(next);
    onQueryChange(next.trim());
    if (next.trim().length === 0) {
      setEverywhere(false);
      setFilters({});
    }
  }

  const { data, isFetching } = useQuery({
    queryKey: ["search", roomId ?? null, sectionId, debounced, everywhere],
    queryFn: () =>
      api.search(everywhere ? { q: debounced, everywhere: true } : { q: debounced, roomId, sectionId, everywhere: false }),
    enabled: debounced.length > 0,
  });

  const active = value.trim().length > 0;
  // Chips are built from the hits, then applied to them - so every option leaves something behind, and
  // narrowing is instant rather than a round trip.
  const facets = useMemo(() => facetsOf(data?.recordings ?? []), [data]);
  const visible = useMemo(() => applyFilters(data?.recordings ?? [], filters), [data, filters]);
  const groups = useMemo(() => groupBySection(visible), [visible]);

  return (
    // Only claims the panel's spare height once results are showing; idle, it is just the pinned field and
    // the drill-in list below keeps the room.
    <div className={`flex min-h-0 flex-col ${active ? "flex-1" : "shrink-0"}`}>
      <div className="shrink-0 border-b px-2 py-2 dark:border-gray-800">
        <div className="flex items-center gap-1.5 rounded-lg border bg-gray-50 px-2 py-1 focus-within:ring-2 focus-within:ring-blue-500 dark:border-gray-700 dark:bg-gray-800">
          <span className="shrink-0 text-gray-400">
            <SearchIcon size={14} />
          </span>
          {/* The scope chip earns its place only inside a folder, where the scope is narrower than people
              assume. At the room's top level "search" already means "this room", and spelling that out costs
              the field most of its width (a room named "Platform Administrator" left 57px to type in).
              max-w keeps a long folder name from doing the same. */}
          {everywhere ? (
            <span className="flex shrink-0 items-center gap-1 rounded bg-blue-50 px-1.5 py-0.5 text-[10px] font-medium text-blue-700 dark:bg-blue-900/40 dark:text-blue-300">
              <GlobeIcon size={10} />
              {t("searchScopeEverywhere")}
            </span>
          ) : (
            sectionId !== null && (
              <span
                title={t("searchScopeIn", { name: scopeName })}
                className="flex max-w-[45%] shrink-0 items-center gap-1 truncate rounded bg-blue-50 px-1.5 py-0.5 text-[10px] font-medium text-blue-700 dark:bg-blue-900/40 dark:text-blue-300"
              >
                <span className="shrink-0">
                  <FolderIcon size={10} />
                </span>
                <span className="truncate">{t("searchScopeIn", { name: scopeName })}</span>
              </span>
            )
          )}
          <input
            type="search"
            role="searchbox"
            value={value}
            onChange={(e) => change(e.target.value)}
            onKeyDown={(e) => e.key === "Escape" && change("")}
            placeholder={t("searchPlaceholder")}
            aria-label={t("searchPlaceholder")}
            className="min-w-0 flex-1 bg-transparent text-xs outline-none dark:text-gray-100"
          />
          {active && (
            <button
              type="button"
              aria-label={t("searchClear")}
              onClick={() => change("")}
              className="shrink-0 rounded px-1 text-xs text-gray-400 hover:text-gray-700 dark:hover:text-gray-200"
            >
              ✕
            </button>
          )}
        </div>
      </div>

      {active && (
        <>
          {/* Scoped: how many, and the one tap that widens the net. Promoting is a deliberate act, so the
              count is next to it - you can see what the narrow search found before deciding to widen. */}
          {!everywhere && (
            <div className="flex shrink-0 items-center justify-between gap-2 px-2 py-1">
              <span className="truncate text-[10px] text-gray-500 dark:text-gray-400">
                {t("searchCountIn", { count: visible.length, name: scopeName })}
              </span>
              <button
                type="button"
                onClick={() => setEverywhere(true)}
                className="shrink-0 text-[10px] text-blue-600 hover:underline dark:text-blue-400"
              >
                {t("searchEverywhere")}
              </button>
            </div>
          )}

          {/* Chips only once global: a folder-scoped search is already narrow, and offering to narrow it
              further is noise. */}
          {everywhere && (facets.sections.length > 0 || facets.speakers.length > 0) && (
            <SearchFilters facets={facets} filters={filters} onChange={setFilters} />
          )}

          <div className="min-h-0 flex-1 overflow-y-auto">
          {data?.folders.map((f) => (
            <button
              key={f.id}
              type="button"
              aria-label={t("drillOpenFolder", { name: f.name })}
              onClick={() => onDrill?.(f.id)}
              style={
                {
                  "--sc-light": sectionColor(f.id).light,
                  "--sc-dark": sectionColor(f.id).dark,
                } as React.CSSProperties
              }
              className="flex w-full items-center gap-1.5 border-b px-2 py-1.5 text-left text-[var(--sc-light)] hover:bg-gray-50 dark:border-gray-800 dark:text-[var(--sc-dark)] dark:hover:bg-gray-800/60"
            >
              <FolderIcon size={14} />
              <span className="min-w-0 flex-1 truncate text-[12.5px] font-semibold">{f.name}</span>
              <span className="shrink-0 text-[11px] tabular-nums text-gray-400">{f.recordingCount}</span>
              <span className="shrink-0 text-gray-400">
                <ChevronRightIcon size={14} />
              </span>
            </button>
          ))}

          {/* Scoped results are a flat list - they are all from one folder, so a heading saying so on every
              row would be noise. Global results are grouped, because "where is this?" is the first question. */}
          {everywhere
            ? groups.map((g) => (
                <div key={g.id}>
                  <h3
                    style={
                      g.id === UNGROUPED_GROUP
                        ? undefined
                        : ({ "--sc-light": sectionColor(g.id).light, "--sc-dark": sectionColor(g.id).dark } as React.CSSProperties)
                    }
                    className={`flex items-center gap-1 border-b bg-gray-50 px-2 py-1 text-[10px] font-semibold uppercase tracking-wide dark:border-gray-800 dark:bg-gray-800/60 ${
                      g.id === UNGROUPED_GROUP
                        ? "text-gray-500 dark:text-gray-400"
                        : "text-[var(--sc-light)] dark:text-[var(--sc-dark)]"
                    }`}
                  >
                    <FolderIcon size={10} />
                    <span className="min-w-0 flex-1 truncate">{g.name ?? t("ungrouped")}</span>
                    <span className="shrink-0 tabular-nums opacity-70">{g.hits.length}</span>
                  </h3>
                  {g.hits.map((hit) => (
                    // The group header already names this folder, so a row only shows its path when the path
                    // says something more - i.e. it has parents the header doesn't mention.
                    <Hit
                      key={hit.recordingId}
                      hit={hit}
                      query={debounced}
                      showBreadcrumb={hit.breadcrumb.length > 1}
                    />
                  ))}
                </div>
              ))
            : visible.map((hit) => <Hit key={hit.recordingId} hit={hit} query={debounced} />)}

          {data && data.folders.length === 0 && visible.length === 0 && !isFetching && (
            <p className="p-4 text-xs text-gray-500 dark:text-gray-400">{t("searchNoMatches")}</p>
          )}
          </div>
        </>
      )}
    </div>
  );
}

/// One recording hit: where it lives, what was said, and a link straight to that moment. `showBreadcrumb` is
/// false under a group header that already names the folder.
function Hit({
  hit,
  query,
  showBreadcrumb = true,
}: {
  hit: RecordingSearchHit;
  query: string;
  showBreadcrumb?: boolean;
}) {
  const basePath = useRoomBasePath();
  const drillSearch = useDrillSearch();
  // `?ts=` is how the transcript page deep-links to a match, and the drill rides along so coming back lands
  // where you were. Built by hand rather than via useDrillSearch's string, since both params are needed.
  const params = new URLSearchParams(drillSearch.replace(/^\?/, ""));
  params.set("ts", String(hit.snippetStartMs));

  return (
    <Link
      to={{ pathname: `${basePath}/recordings/${hit.recordingId}`, search: `?${params}` }}
      className="block border-b px-2 py-1.5 hover:bg-gray-50 dark:border-gray-800 dark:hover:bg-gray-800/60"
    >
      <span className="flex items-baseline gap-2">
        <span className="min-w-0 flex-1 truncate text-[12.5px] text-gray-900 dark:text-gray-100">{hit.name}</span>
        <span className="shrink-0 text-[11px] tabular-nums text-gray-400">{formatDuration(hit.durationMs)}</span>
      </span>
      {hit.snippet && (
        <span className="mt-0.5 block truncate text-[10.5px] text-gray-500 dark:text-gray-400">
          {/* Window before highlighting: the row is one truncated line, so an un-windowed segment would clip
              the matched words off the end and show a snippet that never says why it matched. */}
          {highlight(snippetWindow(hit.snippet, query), query).map((part, i) =>
            part.match ? (
              <mark key={i} className="bg-amber-100 text-amber-800 dark:bg-amber-400/10 dark:text-amber-300">
                {part.text}
              </mark>
            ) : (
              <span key={i}>{part.text}</span>
            ),
          )}
        </span>
      )}
      {showBreadcrumb && hit.breadcrumb.length > 0 && (
        <span className="mt-0.5 block truncate text-[10px] text-gray-400 dark:text-gray-500">
          {hit.breadcrumb.join(" › ")}
        </span>
      )}
    </Link>
  );
}
