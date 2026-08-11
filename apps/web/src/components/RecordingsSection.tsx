import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { RecordingPlacementMode } from "../lib/types";
import FolderPicker from "./FolderPicker";
import { usePreferencesFooter } from "./PreferencesFooter";

/// Mirrors the server-side defaults (`UserSettings.DefaultCalendar*`). Duplicated rather than derived
/// because the field can be blank while typing, and a blank must fall back to something on save.
const DEFAULT_AFTER_MINUTES = 3;
const DEFAULT_SILENCE_SECONDS = 30;

/// A whole number above zero, or the default. Guards the two auto-stop durations: a blanked field ("" -> NaN)
/// or a zero would tell the recorder to stop the moment it started.
function positiveOr(value: string, fallback: number): number {
  const n = Number.parseInt(value, 10);
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

/// The five values as last loaded or last saved. `dirty` is a comparison against this rather than a flag
/// set by each change handler, so undoing an edit by hand clears the indicator instead of latching it.
/// Reset from the payload on a successful save rather than waiting for the refetch, which would otherwise
/// leave the footer briefly reading "Unsaved changes" over values that are already stored.
interface Baseline {
  placementMode: RecordingPlacementMode;
  placementSectionId: string | null;
  calendarAutoStop: boolean;
  afterMinutes: number;
  silenceSeconds: number;
}

/// Recordings tab: where a new recording lands in the user's Personal room (Ungrouped / the selected folder /
/// a fixed folder), plus how a recording started from a calendar event should end. Self-contained; its Save
/// PUTs only the fields this tab owns (tri-state), leaving the other personal preferences untouched. Save
/// itself lives in the modal's shared footer (see `usePreferencesFooter`), not in this component's body.
export default function RecordingsSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });
  // Personal-room folders for the "Use a specific folder" chooser (flattened "Parent › Child").
  const { data: sections = [] } = useQuery({ queryKey: ["sections"], queryFn: () => api.listSections() });

  const [placementMode, setPlacementMode] = useState<RecordingPlacementMode>("SelectedFolder");
  const [placementSectionId, setPlacementSectionId] = useState<string | null>(null);
  // Calendar-event auto-stop. The two durations are held as strings so the field can be emptied while
  // typing; `positiveOr` turns a blank or nonsensical value back into the default at save time.
  const [calendarAutoStop, setCalendarAutoStop] = useState(false);
  const [afterMinutes, setAfterMinutes] = useState(String(DEFAULT_AFTER_MINUTES));
  const [silenceSeconds, setSilenceSeconds] = useState(String(DEFAULT_SILENCE_SECONDS));
  const [error, setError] = useState<string | null>(null);
  const [savedOnce, setSavedOnce] = useState(false);
  const [busy, setBusy] = useState(false);
  const [baseline, setBaseline] = useState<Baseline | null>(null);

  useEffect(() => {
    if (data) {
      const next: Baseline = {
        placementMode: data.placementMode ?? "SelectedFolder",
        placementSectionId: data.placementSectionId ?? null,
        calendarAutoStop: data.calendarAutoStopEnabled ?? false,
        afterMinutes: data.calendarAutoStopAfterMinutes ?? DEFAULT_AFTER_MINUTES,
        silenceSeconds: data.calendarSilenceStopSeconds ?? DEFAULT_SILENCE_SECONDS,
      };
      setPlacementMode(next.placementMode);
      setPlacementSectionId(next.placementSectionId);
      setCalendarAutoStop(next.calendarAutoStop);
      setAfterMinutes(String(next.afterMinutes));
      setSilenceSeconds(String(next.silenceSeconds));
      setBaseline(next);
    }
  }, [data]);

  // The exact five fields Save sends, so `dirty` compares what would be stored rather than what is typed:
  // blanking a duration field is not a change, because `positiveOr` would store the same number anyway.
  const current: Baseline = {
    placementMode,
    placementSectionId: placementMode === "SpecificFolder" ? placementSectionId : null,
    calendarAutoStop,
    afterMinutes: positiveOr(afterMinutes, DEFAULT_AFTER_MINUTES),
    silenceSeconds: positiveOr(silenceSeconds, DEFAULT_SILENCE_SECONDS),
  };
  const dirty =
    baseline !== null &&
    (current.placementMode !== baseline.placementMode ||
      current.placementSectionId !== baseline.placementSectionId ||
      current.calendarAutoStop !== baseline.calendarAutoStop ||
      current.afterMinutes !== baseline.afterMinutes ||
      current.silenceSeconds !== baseline.silenceSeconds);

  usePreferencesFooter({
    dirty,
    busy,
    status: dirty ? "unsaved" : savedOnce ? "saved" : "idle",
    error,
    onSave,
  });

  // Render only once the settings have loaded, so an early interaction can't be overwritten by the arriving
  // initial values (the effect above seeds state from `data`).
  if (!data) return null;

  async function onSave() {
    setError(null);
    setBusy(true);
    try {
      await api.updateUserSettings({
        placementMode: current.placementMode,
        placementSectionId: current.placementSectionId,
        calendarAutoStopEnabled: current.calendarAutoStop,
        calendarAutoStopAfterMinutes: current.afterMinutes,
        calendarSilenceStopSeconds: current.silenceSeconds,
      });
      qc.invalidateQueries({ queryKey: ["user-settings"] });
      // Show the coerced values, so a field left blank reads as the default that was actually stored.
      setAfterMinutes(String(current.afterMinutes));
      setSilenceSeconds(String(current.silenceSeconds));
      setBaseline(current);
      setSavedOnce(true);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-3">
      <p className="text-xs text-gray-500 dark:text-gray-400">{t("recordingsIntro")}</p>
      <fieldset className="space-y-2">
        <label className="flex items-start gap-2 text-sm">
          <input
            type="radio"
            name="placement-mode"
            className="mt-0.5"
            checked={placementMode === "Ungrouped"}
            onChange={() => setPlacementMode("Ungrouped")}
          />
          <span className="text-gray-700 dark:text-gray-200">{t("placementUngrouped")}</span>
        </label>
        <label className="flex items-start gap-2 text-sm">
          <input
            type="radio"
            name="placement-mode"
            className="mt-0.5"
            checked={placementMode === "SelectedFolder"}
            onChange={() => setPlacementMode("SelectedFolder")}
          />
          <span className="text-gray-700 dark:text-gray-200">{t("placementSelected")}</span>
        </label>
        <label className="flex items-start gap-2 text-sm">
          <input
            type="radio"
            name="placement-mode"
            className="mt-0.5"
            checked={placementMode === "SpecificFolder"}
            onChange={() => setPlacementMode("SpecificFolder")}
          />
          <span className="text-gray-700 dark:text-gray-200">{t("placementSpecific")}</span>
        </label>
      </fieldset>
      {placementMode === "SpecificFolder" && (
        <div className="block text-sm">
          <span id="placement-folder-label" className="mb-1 block text-gray-600 dark:text-gray-300">
            {t("placementFolder")}
          </span>
          {/* `FolderPicker` is a multi-control widget (filter box, drill list, per-row select targets), not
              a single form field, so it can't carry the visible label the way the native <select> it
              replaces did via a plain `aria-label`. `role="group"` + `aria-labelledby` keeps the same
              visible "Folder" text as the programmatic name (assistive tech announces it entering the
              group), while the picker's own inner controls keep their own more specific labels
              ("Filter folders", "Select {name}") - see FolderPicker.tsx for why those exist. */}
          <div role="group" aria-labelledby="placement-folder-label">
            <FolderPicker sections={sections} selectedId={placementSectionId} onSelect={setPlacementSectionId} />
          </div>
        </div>
      )}

      {/* Recording started from a calendar event: the only case where the meeting's end time is known, so
          the only case where the recorder can end a take by itself. */}
      <div className="space-y-2 border-t pt-3 dark:border-gray-700">
        <h3 className="text-sm font-medium text-gray-700 dark:text-gray-200">{t("calendarRecordingHeading")}</h3>
        <p className="text-xs text-gray-500 dark:text-gray-400">{t("calendarRecordingIntro")}</p>

        <label className="flex items-start gap-2 text-sm">
          <input
            type="checkbox"
            className="mt-0.5"
            checked={calendarAutoStop}
            onChange={(e) => setCalendarAutoStop(e.target.checked)}
          />
          <span className="text-gray-700 dark:text-gray-200">{t("calendarAutoStop")}</span>
        </label>

        {/* Both conditions say HOW the recording ends, not WHETHER - so they are inert, and disabled, until
            the checkbox above is on. */}
        <label className="block text-sm">
          <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("calendarAfterMinutes")}</span>
          <input
            type="number"
            min={1}
            step={1}
            value={afterMinutes}
            disabled={!calendarAutoStop}
            onChange={(e) => setAfterMinutes(e.target.value)}
            aria-label={t("calendarAfterMinutes")}
            className="w-24 rounded border px-2 py-1 text-sm disabled:opacity-50 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">{t("calendarAfterMinutesHint")}</span>
        </label>

        <label className="block text-sm">
          <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("calendarSilenceSeconds")}</span>
          <input
            type="number"
            min={1}
            step={1}
            value={silenceSeconds}
            disabled={!calendarAutoStop}
            onChange={(e) => setSilenceSeconds(e.target.value)}
            aria-label={t("calendarSilenceSeconds")}
            className="w-24 rounded border px-2 py-1 text-sm disabled:opacity-50 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">{t("calendarSilenceSecondsHint")}</span>
        </label>
      </div>
    </div>
  );
}
