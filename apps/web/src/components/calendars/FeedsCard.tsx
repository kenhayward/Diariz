import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { GlobeIcon } from "../icons";
import SourceCard, { cardBtn } from "./SourceCard";
import type { IcsFeed } from "../../lib/types";

const DEFAULT_COLOR = "#7986CB"; // Google "Lavender"

/// Subscribed iCalendar (.ics) feeds - public team or shared calendars not reachable through an account,
/// so their events show on the Calendar tab in the colour chosen here. The server validates and
/// test-fetches each URL, so a broken or unsafe one is rejected on add. Works with or without an account.
///
/// The add/edit form sits behind "+ Add feed": as an always-visible block it occupied a third of the old
/// tab while empty. `Edit` opens the same form prefilled - one form, two modes, as before.
export default function FeedsCard() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: feeds } = useQuery({ queryKey: ["calendar-feeds"], queryFn: api.listCalendarFeeds });

  const [open, setOpen] = useState(false);
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
    setOpen(false);
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
    setOpen(true);
  }

  const shown = feeds?.filter((f) => f.enabled).length ?? 0;
  const canSubmit = name.trim().length > 0 && url.trim().length > 0 && !busy;
  const input = "w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100";

  return (
    <SourceCard
      name={t("calendarsFeedsName")}
      meta={t("calendarsFeedsMeta")}
      tint={DEFAULT_COLOR}
      glyph={GlobeIcon}
      status={shown > 0 ? t("calendarsStatusShown", { count: shown }) : undefined}
      error={error}
      actions={
        <button type="button" onClick={() => (open ? resetForm() : setOpen(true))} className={cardBtn}>
          {t("calendarsFeedsAdd")}
        </button>
      }
    >
      {open && (
        <div className="mb-2 space-y-1.5 rounded border border-dashed border-gray-300 p-2 dark:border-gray-700">
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={t("calFeedNamePlaceholder")}
            aria-label={t("calFeedNamePlaceholder")}
            className={input}
          />
          <div className="flex gap-2">
            <input
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              placeholder={t("calFeedUrlPlaceholder")}
              aria-label={t("calFeedUrlPlaceholder")}
              className={`min-w-0 flex-1 ${input}`}
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
            <button type="button" onClick={submit} disabled={!canSubmit} className={cardBtn}>
              {editingId ? t("common:save") : t("calFeedAdd")}
            </button>
            <button type="button" onClick={resetForm} className={cardBtn}>
              {t("common:cancel")}
            </button>
          </div>
        </div>
      )}

      <ul>
        {feeds?.map((f) => (
          <li key={f.id} className="border-b border-gray-100 py-1.5 text-xs last:border-b-0 dark:border-gray-800">
            <div className="flex items-center gap-2">
              <span
                aria-hidden
                className="inline-block h-2.5 w-2.5 shrink-0 rounded-sm border dark:border-gray-600"
                style={{ backgroundColor: f.color ?? DEFAULT_COLOR }}
              />
              <div className="min-w-0 flex-1">
                <div className={`truncate text-[13px] ${f.enabled ? "text-gray-800 dark:text-gray-200" : "text-gray-400 dark:text-gray-500"}`}>
                  {f.name}
                </div>
                <div className="truncate text-[11px] text-gray-400 dark:text-gray-500">{f.url}</div>
              </div>
              <label className="flex shrink-0 items-center gap-1 text-gray-500 dark:text-gray-400">
                {/* The visible word is "Shown"; the accessible name says which feed, because the Outlook
                    card's machines carry the very same word two cards down. */}
                <input
                  type="checkbox"
                  checked={f.enabled}
                  onChange={() => toggleEnabled(f)}
                  aria-label={t("calFeedShownAria", { name: f.name })}
                />
                {t("calFeedShown")}
              </label>
              <button
                type="button"
                onClick={() => startEdit(f)}
                aria-label={t("calFeedEditAria", { name: f.name })}
                className="shrink-0 text-blue-600 hover:underline dark:text-blue-400"
              >
                {t("calFeedEdit")}
              </button>
              <button
                type="button"
                onClick={() => remove(f.id)}
                aria-label={t("calFeedRemoveAria", { name: f.name })}
                className="shrink-0 text-red-600 hover:underline dark:text-red-400"
              >
                {t("calFeedRemove")}
              </button>
            </div>
            {f.lastError && (
              <p className="ml-4.5 mt-0.5 text-[11px] text-red-600 dark:text-red-400">
                {t("calFeedBroken", { error: f.lastError })}
              </p>
            )}
          </li>
        ))}
        {feeds?.length === 0 && <li className="text-xs text-gray-400 dark:text-gray-500">{t("calFeedNone")}</li>}
      </ul>
    </SourceCard>
  );
}
