import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { LlmCallKind, LlmUsageFilterOptions } from "../../lib/types";

export type DatePreset = "24h" | "7d" | "30d" | "custom";

/// The filter bar's own state (a superset of `LlmUsageFilter` - `preset` is UI-only, dropped before the
/// request goes out). `from`/`to` are ISO 8601 strings, same shape the API expects, so the page can pass
/// them straight through to `api.getLlmUsage`/`getLlmUsageFilters`.
export interface UsageFilterState {
  preset: DatePreset;
  from: string | null;
  to: string | null;
  userIds: string[];
  kinds: LlmCallKind[];
  models: string[];
  outcome: "ok" | "failed" | "all";
}

const PRESET_HOURS: Record<Exclude<DatePreset, "custom">, number> = { "24h": 24, "7d": 24 * 7, "30d": 24 * 30 };

/// The ISO `from` timestamp for a canned preset, `hours` before `now`. Exported so the page's initial
/// filter and the preset buttons below compute it the same way. `now` is injectable for deterministic tests.
export function presetFrom(preset: Exclude<DatePreset, "custom">, now: Date = new Date()): string {
  return new Date(now.getTime() - PRESET_HOURS[preset] * 3600_000).toISOString();
}

/// The filter shown when the page first opens: the last 7 days, every user/type/model, every outcome.
export function defaultUsageFilter(now: Date = new Date()): UsageFilterState {
  return { preset: "7d", from: presetFrom("7d", now), to: null, userIds: [], kinds: [], models: [], outcome: "all" };
}

/// A call kind's display label. Exported so the table can render the same label in each row's Type column
/// as the filter checkbox that name matches.
export function kindLabel(t: (key: string) => string, kind: LlmCallKind): string {
  return t(`llmUsageKind${kind}`);
}

/// Date presets, user/type/model multi-selects (populated from `GET /filters` - only values that actually
/// occur in the scoped set, never the full enum/user list), and an outcome toggle. All-controlled: the page
/// owns `filter` and gets every change back through `onChange` - this component holds no filter state of
/// its own (only each multi-select's open/closed UI state).
export default function UsageFilterBar({
  filter,
  onChange,
  filterOptions,
}: {
  filter: UsageFilterState;
  onChange: (next: UsageFilterState) => void;
  filterOptions: LlmUsageFilterOptions | undefined;
}) {
  const { t } = useTranslation("account");

  function pickPreset(preset: Exclude<DatePreset, "custom">) {
    onChange({ ...filter, preset, from: presetFrom(preset), to: null });
  }

  function presetButton(preset: DatePreset, labelKey: string) {
    const active = filter.preset === preset;
    return (
      <button
        type="button"
        key={preset}
        aria-pressed={active}
        onClick={() => (preset === "custom" ? onChange({ ...filter, preset: "custom" }) : pickPreset(preset))}
        className={`rounded-full border px-2.5 py-1 text-xs font-medium ${
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
    <div className="flex flex-wrap items-center gap-2 border-b px-3 py-2 dark:border-gray-800">
      <div className="flex items-center gap-1">
        {presetButton("24h", "llmUsagePreset24h")}
        {presetButton("7d", "llmUsagePreset7d")}
        {presetButton("30d", "llmUsagePreset30d")}
        {presetButton("custom", "llmUsagePresetCustom")}
      </div>

      {filter.preset === "custom" && (
        <div className="flex items-center gap-2 text-xs text-gray-600 dark:text-gray-300">
          <label className="flex items-center gap-1">
            {t("llmUsageCustomFrom")}
            <input
              type="date"
              aria-label={t("llmUsageCustomFrom")}
              value={filter.from ? filter.from.slice(0, 10) : ""}
              onChange={(e) =>
                onChange({ ...filter, from: e.target.value ? new Date(e.target.value).toISOString() : null })
              }
              className="rounded border px-1 py-0.5 dark:border-gray-700 dark:bg-gray-800"
            />
          </label>
          <label className="flex items-center gap-1">
            {t("llmUsageCustomTo")}
            <input
              type="date"
              aria-label={t("llmUsageCustomTo")}
              value={filter.to ? filter.to.slice(0, 10) : ""}
              onChange={(e) =>
                onChange({ ...filter, to: e.target.value ? new Date(e.target.value).toISOString() : null })
              }
              className="rounded border px-1 py-0.5 dark:border-gray-700 dark:bg-gray-800"
            />
          </label>
        </div>
      )}

      <MultiSelect
        label={t("llmUsageFilterUsers")}
        options={(filterOptions?.users ?? []).map((u) => ({ value: u.userId, label: u.userEmail }))}
        selected={filter.userIds}
        onChange={(userIds) => onChange({ ...filter, userIds })}
      />
      <MultiSelect
        label={t("llmUsageFilterKinds")}
        options={(filterOptions?.kinds ?? []).map((k) => ({ value: k, label: kindLabel(t, k) }))}
        selected={filter.kinds}
        onChange={(kinds) => onChange({ ...filter, kinds: kinds as LlmCallKind[] })}
      />
      <MultiSelect
        label={t("llmUsageFilterModels")}
        options={(filterOptions?.models ?? []).map((m) => ({ value: m, label: m }))}
        selected={filter.models}
        onChange={(models) => onChange({ ...filter, models })}
      />

      <label className="ml-auto flex items-center gap-1 text-xs text-gray-600 dark:text-gray-300">
        {t("llmUsageFilterOutcome")}
        <select
          value={filter.outcome}
          aria-label={t("llmUsageFilterOutcome")}
          onChange={(e) => onChange({ ...filter, outcome: e.target.value as UsageFilterState["outcome"] })}
          className="rounded border px-1 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800"
        >
          <option value="all">{t("llmUsageOutcomeAll")}</option>
          <option value="ok">{t("llmUsageOutcomeOk")}</option>
          <option value="failed">{t("llmUsageOutcomeFailed")}</option>
        </select>
      </label>
    </div>
  );
}

/// A dropdown of checkboxes standing in for a multi-select - there is no multi-select component anywhere
/// in this codebase, and pulling in a dependency for one filter bar isn't worth it. Mirrors the
/// click-outside-to-close menu pattern `nav/SearchFilters.tsx`'s `Chip` already uses, just with checkboxes
/// instead of a single pick so more than one value can be active at once.
function MultiSelect({
  label,
  options,
  selected,
  onChange,
}: {
  label: string;
  options: { value: string; label: string }[];
  selected: string[];
  onChange: (next: string[]) => void;
}) {
  const { t } = useTranslation("account");
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  function toggle(value: string) {
    onChange(selected.includes(value) ? selected.filter((v) => v !== value) : [...selected, value]);
  }

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className={`rounded-full border px-2.5 py-1 text-xs font-medium ${
          selected.length > 0
            ? "border-blue-300 bg-blue-50 text-blue-700 dark:border-blue-800 dark:bg-blue-900/40 dark:text-blue-300"
            : "border-gray-200 text-gray-600 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
        }`}
      >
        {label}
        {selected.length > 0 ? ` (${selected.length})` : ""}
      </button>
      {open && (
        <div
          role="listbox"
          aria-label={label}
          className="absolute left-0 z-30 mt-1 max-h-56 w-56 overflow-y-auto rounded-lg border bg-white p-1 shadow-lg dark:border-gray-700 dark:bg-gray-900"
        >
          {options.length === 0 ? (
            <p className="px-2 py-1 text-xs text-gray-400 dark:text-gray-500">{t("llmUsageFilterNoOptions")}</p>
          ) : (
            options.map((o) => (
              <label
                key={o.value}
                className="flex items-center gap-2 rounded px-2 py-1 text-xs hover:bg-gray-50 dark:hover:bg-gray-800"
              >
                <input type="checkbox" checked={selected.includes(o.value)} onChange={() => toggle(o.value)} />
                <span className="truncate">{o.label}</span>
              </label>
            ))
          )}
        </div>
      )}
    </div>
  );
}
