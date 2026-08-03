import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { FeedbackDto } from "../lib/types";

/// A trail entry as it might arrive on the wire. `trailJson` is client-supplied and stored verbatim
/// (see `lib/trail.ts` for the shape the current client actually sends), so nothing about its structure
/// can be trusted here - every field is read defensively.
type TrailEntryLike = {
  kind?: unknown;
  label?: unknown;
  detail?: unknown;
};

/// Parses one row's `trailJson` defensively. A malformed value (bad JSON, not an array, a future/foreign
/// shape) becomes an empty trail for that row alone rather than throwing - the parse happens per row, so
/// one bad submission never keeps the rest of the list from rendering or expanding.
function parseTrail(trailJson: string): TrailEntryLike[] {
  if (!trailJson || !trailJson.trim()) return [];
  try {
    const parsed: unknown = JSON.parse(trailJson);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((e): e is TrailEntryLike => e !== null && typeof e === "object");
  } catch {
    return [];
  }
}

function entryKind(e: TrailEntryLike): string {
  return typeof e.kind === "string" && e.kind ? e.kind : "?";
}

function entryLabel(e: TrailEntryLike): string {
  return typeof e.label === "string" ? e.label : "";
}

/// Renders an entry's `detail` bag as "key: value, key: value" - readable in a line, unlike a raw JSON dump.
function entryDetail(e: TrailEntryLike): string | null {
  if (!e.detail || typeof e.detail !== "object" || Array.isArray(e.detail)) return null;
  const parts = Object.entries(e.detail as Record<string, unknown>).map(
    ([k, v]) => `${k}: ${typeof v === "string" ? v : JSON.stringify(v)}`,
  );
  return parts.length ? parts.join(", ") : null;
}

/// Platform-Administrator-only Feedback tab: lists every "Provide Feedback" submission (`api.listFeedback`,
/// server already orders newest first) and lets the admin delete one. A submission's value is the trail
/// alongside the words, so each row can expand to show its parsed `trailJson` as a readable per-entry list
/// (kind + label + detail) rather than a raw blob. Delete is a two-step inline confirm (Delete -> Confirm/
/// Cancel) rather than `window.confirm`, so it reads consistently inside the row it acts on.
export default function FeedbackPanel() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: items = [], isLoading } = useQuery({ queryKey: ["feedback"], queryFn: api.listFeedback });
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [confirmingId, setConfirmingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const remove = useMutation({
    mutationFn: (id: string) => api.deleteFeedback(id),
    onSuccess: () => {
      setConfirmingId(null);
      setError(null);
      void qc.invalidateQueries({ queryKey: ["feedback"] });
    },
    onError: (e) => setError(apiErrorMessage(e)),
  });

  // Defensive re-sort: the server already returns newest first, but the panel's own claim ("newest first")
  // shouldn't silently depend on that never changing.
  const sorted: FeedbackDto[] = [...items].sort(
    (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
  );

  const iconBtn =
    "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="space-y-3">
      <p className="text-xs text-gray-500 dark:text-gray-400">{t("feedbackIntro")}</p>
      {error && <p className="text-xs text-red-600 dark:text-red-400">{error}</p>}
      {isLoading && <p className="text-xs text-gray-400 dark:text-gray-500">{t("common:loading")}</p>}
      {!isLoading && sorted.length === 0 && (
        <p className="text-xs text-gray-400 dark:text-gray-500">{t("feedbackEmpty")}</p>
      )}
      <ul className="space-y-2">
        {sorted.map((row) => {
          const expanded = expandedId === row.id;
          const confirming = confirmingId === row.id;
          const trail = expanded ? parseTrail(row.trailJson) : [];
          return (
            <li key={row.id} data-testid="feedback-row" className="rounded border p-2 text-sm dark:border-gray-700">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-baseline gap-x-2">
                    <span className="font-medium text-gray-800 dark:text-gray-100">
                      {row.userEmail ?? t("feedbackUnknownUser")}
                    </span>
                    <span className="text-xs text-gray-400 dark:text-gray-500">
                      {new Date(row.createdAt).toLocaleString()}
                    </span>
                  </div>
                  <p className="mt-1 whitespace-pre-wrap text-gray-700 dark:text-gray-200">{row.description}</p>
                  <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">
                    {t("feedbackRouteRelease", { route: row.route, release: row.release })}
                  </p>
                </div>
                <div className="flex shrink-0 items-center gap-1">
                  <button
                    type="button"
                    onClick={() => setExpandedId(expanded ? null : row.id)}
                    className={iconBtn}
                  >
                    {expanded ? t("feedbackHideDetail") : t("feedbackViewDetail")}
                  </button>
                  {confirming ? (
                    <>
                      <button
                        type="button"
                        onClick={() => remove.mutate(row.id)}
                        disabled={remove.isPending}
                        className="rounded bg-red-600 px-2 py-1 text-xs text-white hover:bg-red-700 disabled:opacity-50"
                      >
                        {t("common:confirm")}
                      </button>
                      <button type="button" onClick={() => setConfirmingId(null)} className={iconBtn}>
                        {t("common:cancel")}
                      </button>
                    </>
                  ) : (
                    <button
                      type="button"
                      onClick={() => {
                        setError(null);
                        setConfirmingId(row.id);
                      }}
                      className="rounded px-2 py-1 text-xs text-red-600 hover:bg-red-50 dark:hover:bg-red-900/30"
                    >
                      {t("common:delete")}
                    </button>
                  )}
                </div>
              </div>
              {expanded && (
                <div className="mt-2 border-t pt-2 dark:border-gray-700">
                  {trail.length === 0 ? (
                    <p className="text-xs text-gray-400 dark:text-gray-500">{t("feedbackTrailEmpty")}</p>
                  ) : (
                    <ol className="space-y-1">
                      {trail.map((entry, i) => (
                        <li key={i} className="text-xs text-gray-600 dark:text-gray-300">
                          <span className="mr-1 inline-block rounded bg-gray-100 px-1.5 py-0.5 font-mono uppercase text-gray-500 dark:bg-gray-800 dark:text-gray-400">
                            {entryKind(entry)}
                          </span>
                          <span>{entryLabel(entry)}</span>
                          {entryDetail(entry) && (
                            <span className="ml-1 text-gray-400 dark:text-gray-500">({entryDetail(entry)})</span>
                          )}
                        </li>
                      ))}
                    </ol>
                  )}
                </div>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
