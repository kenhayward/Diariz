import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { api } from "../../lib/api";
import { useRoomBasePath } from "../../lib/rooms";
import { useDrillSearch } from "../../lib/drillRoute";
import { sectionColor } from "../../lib/sectionColors";
import { formatDuration } from "../../lib/format";
import { highlight, snippetWindow } from "../../lib/highlight";
import { SearchIcon, FolderIcon, ChevronRightIcon } from "../icons";
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

  function change(next: string) {
    setValue(next);
    onQueryChange(next.trim());
  }

  const { data, isFetching } = useQuery({
    queryKey: ["search", roomId ?? null, sectionId, debounced],
    queryFn: () => api.search({ q: debounced, roomId, sectionId, everywhere: false }),
    enabled: debounced.length > 0,
  });

  const active = value.trim().length > 0;

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
          {sectionId !== null && (
            <span
              title={t("searchScopeIn", { name: scopeName })}
              className="flex max-w-[45%] shrink-0 items-center gap-1 truncate rounded bg-blue-50 px-1.5 py-0.5 text-[10px] font-medium text-blue-700 dark:bg-blue-900/40 dark:text-blue-300"
            >
              <span className="shrink-0">
                <FolderIcon size={10} />
              </span>
              <span className="truncate">{t("searchScopeIn", { name: scopeName })}</span>
            </span>
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

          {data?.recordings.map((hit) => <Hit key={hit.recordingId} hit={hit} query={debounced} />)}

          {data && data.folders.length === 0 && data.recordings.length === 0 && !isFetching && (
            <p className="p-4 text-xs text-gray-500 dark:text-gray-400">{t("searchNoMatches")}</p>
          )}
        </div>
      )}
    </div>
  );
}

/// One recording hit: where it lives, what was said, and a link straight to that moment.
function Hit({ hit, query }: { hit: RecordingSearchHit; query: string }) {
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
      {hit.breadcrumb.length > 0 && (
        <span className="mt-0.5 block truncate text-[10px] text-gray-400 dark:text-gray-500">
          {hit.breadcrumb.join(" › ")}
        </span>
      )}
    </Link>
  );
}
