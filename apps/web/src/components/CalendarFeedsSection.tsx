import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { IcsFeed } from "../lib/types";

const DEFAULT_COLOR = "#7986CB"; // Google "Lavender"

/// The Preferences "Calendar feeds" section: subscribe to external iCalendar (.ics) feeds - public team or
/// shared calendars not reachable through Google - so their events show on the Calendar tab, tinted with the
/// colour chosen here. The server validates + test-fetches each URL, so a broken/unsafe URL is rejected on add.
/// Works with or without a Google connection.
export default function CalendarFeedsSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: feeds } = useQuery({ queryKey: ["calendar-feeds"], queryFn: api.listCalendarFeeds });

  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [color, setColor] = useState(DEFAULT_COLOR);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const invalidate = () => qc.invalidateQueries({ queryKey: ["calendar-feeds"] });

  function resetForm() {
    setName("");
    setUrl("");
    setColor(DEFAULT_COLOR);
    setEditingId(null);
  }

  async function submit() {
    setError(null);
    setBusy(true);
    try {
      const input = { name: name.trim(), url: url.trim(), color, enabled: true };
      if (editingId) {
        // Preserve the enabled state of the feed being edited.
        const existing = feeds?.find((f) => f.id === editingId);
        await api.updateCalendarFeed(editingId, { ...input, enabled: existing?.enabled ?? true });
      } else {
        await api.createCalendarFeed(input);
      }
      resetForm();
      invalidate();
    } catch (e) {
      setError(apiErrorMessage(e, t("calFeedSaveError")));
    } finally {
      setBusy(false);
    }
  }

  async function toggleEnabled(feed: IcsFeed) {
    setError(null);
    try {
      await api.updateCalendarFeed(feed.id, {
        name: feed.name,
        url: feed.url,
        color: feed.color,
        enabled: !feed.enabled,
      });
      invalidate();
    } catch (e) {
      setError(apiErrorMessage(e, t("calFeedSaveError")));
    }
  }

  async function remove(id: string) {
    setError(null);
    try {
      await api.deleteCalendarFeed(id);
      if (editingId === id) resetForm();
      invalidate();
    } catch (e) {
      setError(apiErrorMessage(e, t("calFeedRemoveError")));
    }
  }

  function startEdit(feed: IcsFeed) {
    setEditingId(feed.id);
    setName(feed.name);
    setUrl(feed.url);
    setColor(feed.color ?? DEFAULT_COLOR);
    setError(null);
  }

  const btn =
    "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";
  const canSubmit = name.trim().length > 0 && url.trim().length > 0 && !busy;

  return (
    <div className="border-t pt-3 dark:border-gray-700">
      <span className="mb-1 block text-sm text-gray-600 dark:text-gray-300">{t("calFeeds")}</span>
      <p className="text-xs text-gray-400 dark:text-gray-500">{t("calFeedsIntro")}</p>

      <div className="mt-2 space-y-1.5">
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={t("calFeedNamePlaceholder")}
          aria-label={t("calFeedNamePlaceholder")}
          className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <div className="flex gap-2">
          <input
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            placeholder={t("calFeedUrlPlaceholder")}
            aria-label={t("calFeedUrlPlaceholder")}
            className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <input
            type="color"
            value={color}
            onChange={(e) => setColor(e.target.value)}
            aria-label={t("calFeedColor")}
            title={t("calFeedColor")}
            className="h-8 w-8 shrink-0 cursor-pointer rounded border p-0.5 dark:border-gray-700 dark:bg-gray-800"
          />
        </div>
        <div className="flex gap-2">
          <button type="button" onClick={submit} disabled={!canSubmit} className={btn}>
            {editingId ? t("common:save") : t("calFeedAdd")}
          </button>
          {editingId && (
            <button type="button" onClick={resetForm} className={btn}>
              {t("common:cancel")}
            </button>
          )}
        </div>
      </div>

      <ul className="mt-2 space-y-1.5">
        {feeds?.map((f) => (
          <li key={f.id} className="text-xs">
            <div className="flex items-center gap-2">
              <span
                aria-hidden
                className="inline-block h-3 w-3 shrink-0 rounded-sm border dark:border-gray-600"
                style={{ backgroundColor: f.color ?? DEFAULT_COLOR }}
              />
              <span className={`min-w-0 flex-1 truncate ${f.enabled ? "text-gray-700 dark:text-gray-200" : "text-gray-400 dark:text-gray-500"}`}>
                {f.name}
              </span>
              <label className="flex shrink-0 items-center gap-1 text-gray-500 dark:text-gray-400">
                <input type="checkbox" checked={f.enabled} onChange={() => toggleEnabled(f)} aria-label={t("calFeedShown")} />
                {t("calFeedShown")}
              </label>
              <button type="button" onClick={() => startEdit(f)} className="shrink-0 text-blue-600 hover:underline dark:text-blue-400">
                {t("calFeedEdit")}
              </button>
              <button type="button" onClick={() => remove(f.id)} className="shrink-0 text-red-600 hover:underline dark:text-red-400">
                {t("calFeedRemove")}
              </button>
            </div>
            {f.lastError && (
              <p className="ml-5 mt-0.5 text-[11px] text-red-600 dark:text-red-400">{t("calFeedBroken", { error: f.lastError })}</p>
            )}
          </li>
        ))}
        {feeds?.length === 0 && <li className="text-xs text-gray-400 dark:text-gray-500">{t("calFeedNone")}</li>}
      </ul>

      {error && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{error}</p>}
    </div>
  );
}
