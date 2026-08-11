import { useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { RecordingPlacementMode } from "../lib/types";
import { sectionPathLabel } from "../lib/sectionTree";
import FolderPickerModal from "./FolderPickerModal";
import { usePreferencesFooter } from "./PreferencesFooter";
import { CalendarIcon, FolderIcon } from "./icons";

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

/// 24h `HH:MM`, `addMinutes` after the given whole hour. Only used by the worked example, whose meeting is
/// a fixed 10:00-11:00, so it takes an hour rather than a date; wraps past midnight so a silly value like
/// 3000 minutes still renders a clock time rather than "35:00".
function clockAfter(hour: number, addMinutes: number): string {
  const total = (hour * 60 + addMinutes) % (24 * 60);
  const h = Math.floor(total / 60);
  const m = total % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
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
  const { t: tWorkspace } = useTranslation("workspace");
  const qc = useQueryClient();
  const { data } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });
  // Personal-room folders for the "Use a specific folder" chooser (flattened "Parent › Child").
  const { data: sections = [] } = useQuery({ queryKey: ["sections"], queryFn: () => api.listSections() });

  const [placementMode, setPlacementMode] = useState<RecordingPlacementMode>("SelectedFolder");
  const [placementSectionId, setPlacementSectionId] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const changeRef = useRef<HTMLButtonElement>(null);
  // Calendar-event auto-stop. The two durations are held as strings so the field can be emptied while
  // typing; `positiveOr` turns a blank or nonsensical value back into the default at save time.
  const [calendarAutoStop, setCalendarAutoStop] = useState(false);
  const [afterMinutes, setAfterMinutes] = useState(String(DEFAULT_AFTER_MINUTES));
  const [silenceSeconds, setSilenceSeconds] = useState(String(DEFAULT_SILENCE_SECONDS));
  const [error, setError] = useState<string | null>(null);
  const [savedOnce, setSavedOnce] = useState(false);
  const [busy, setBusy] = useState(false);
  const [baseline, setBaseline] = useState<Baseline | null>(null);
  // Tracks which `data` object local state was last seeded from, so the block below fires exactly once
  // per freshly loaded/refetched settings row.
  const [seededFrom, setSeededFrom] = useState<typeof data>(undefined);

  // Seeds local editable state and `baseline` from the loaded settings, in the *same* render as `data`
  // first arrives - not in a `useEffect`, which would run one render after the `if (!data) return null`
  // guard below opens up. That gap would let `data` be on screen (radios rendered) for a frame while
  // `baseline` still read null, which is exactly the state the baseline-gated `usePreferencesFooter` call
  // below exists to keep off screen. Calling `setState` directly in the render body (guarded so it only
  // fires when `data` actually changed) is React's documented way to adjust state in response to an
  // incoming value: React discards this render and re-renders immediately with the new state before
  // anything commits or paints, so there is no intermediate frame where `data` is loaded but `baseline`
  // is not.
  if (data && data !== seededFrom) {
    const mode = data.placementMode ?? "SelectedFolder";
    const next: Baseline = {
      placementMode: mode,
      // Mode-gated the same way `current` is below - the two derivations of this value must agree, or a
      // server row with a stale non-null section id under a non-SpecificFolder mode would open the panel
      // already reading "Unsaved changes" with no user action.
      placementSectionId: mode === "SpecificFolder" ? (data.placementSectionId ?? null) : null,
      calendarAutoStop: data.calendarAutoStopEnabled ?? false,
      afterMinutes: data.calendarAutoStopAfterMinutes ?? DEFAULT_AFTER_MINUTES,
      silenceSeconds: data.calendarSilenceStopSeconds ?? DEFAULT_SILENCE_SECONDS,
    };
    setSeededFrom(data);
    setPlacementMode(next.placementMode);
    setPlacementSectionId(next.placementSectionId);
    setCalendarAutoStop(next.calendarAutoStop);
    setAfterMinutes(String(next.afterMinutes));
    setSilenceSeconds(String(next.silenceSeconds));
    setBaseline(next);
  }

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

  // The panel shows the chosen folder's full path unconditionally - the old inline picker could only show
  // a folder that happened to be at its current drill level, so a deeply nested choice looked unset.
  const chosenPath = sectionPathLabel(sections, placementSectionId, tWorkspace("ungrouped"));

  // `null` until `baseline` is populated (the settings have loaded and the seeding block above has run for
  // them), so no Save button is reachable over a still-loading panel - clicking one before then would PUT
  // this component's hardcoded initial state (defaults), silently overwriting the user's real settings.
  usePreferencesFooter(
    baseline === null
      ? null
      : {
          dirty,
          busy,
          status: dirty ? "unsaved" : savedOnce ? "saved" : "idle",
          error,
          onSave,
        },
  );

  // Render only once the settings have loaded, so an early interaction can't be overwritten by the arriving
  // initial values (the block above seeds state from `data`, in the same render).
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
    <>
      <div className="space-y-3">
        <div className="flex items-baseline gap-2">
          <h3 className="text-[15px] font-semibold dark:text-gray-100">{t("placementHeading")}</h3>
          <span className="text-xs text-gray-500 dark:text-gray-400">{t("placementHeadingMeta")}</span>
        </div>

        <fieldset className="flex flex-col gap-2.5">
          {(
            [
              { mode: "SelectedFolder", title: "placementSelected", meta: "placementSelectedMeta", isDefault: true },
              { mode: "Ungrouped", title: "placementUngrouped", meta: "placementUngroupedMeta", isDefault: false },
              { mode: "SpecificFolder", title: "placementSpecific", meta: "placementSpecificMeta", isDefault: false },
            ] as const
          ).map((card) => (
            <div
              key={card.mode}
              // The selected state is the card's OWN border and background, never an outset ring. The
              // content pane scrolls, and a ring painted 1px outside the box makes the pane wider than its
              // client width, which paints a full-width horizontal scrollbar across the whole panel.
              //
              // A plain <div>, not a <label>: the reveal row below (chosen-path chip + Change button) is a
              // sibling of the <label>, not inside it. A <label> computes its control's accessible name
              // from its entire subtree text, and this row's chip text changes with the chosen folder - if
              // it lived inside the label, the SpecificFolder radio's name would too, on every selection.
              className={`rounded-lg border px-3.5 py-3 ${
                placementMode === card.mode
                  ? "border-blue-500/60 bg-blue-500/[.07] dark:border-blue-500/60 dark:bg-blue-500/[.14]"
                  : "border-gray-200 dark:border-gray-700"
              }`}
            >
              <label className="flex cursor-pointer items-start gap-3">
                <input
                  type="radio"
                  name="placement-mode"
                  className="mt-0.5 accent-blue-600"
                  checked={placementMode === card.mode}
                  onChange={() => setPlacementMode(card.mode)}
                />
                <div className="flex min-w-0 flex-col gap-[3px]">
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium text-gray-900 dark:text-gray-100">{t(card.title)}</span>
                    {card.isDefault && (
                      <span className="rounded border border-blue-500/40 bg-blue-500/10 px-1.5 py-px text-[10px] uppercase tracking-[.06em] text-blue-700 dark:text-blue-200">
                        {t("placementDefaultChip")}
                      </span>
                    )}
                  </div>
                  <span className="text-[13px] text-gray-500 dark:text-gray-400">{t(card.meta)}</span>
                </div>
              </label>
              {card.mode === "SpecificFolder" && placementMode === "SpecificFolder" && (
                // Indented to line up under the card title rather than the radio. A sibling of the <label>
                // above, not nested in it - see the comment on the enclosing <div>.
                <div className="mt-2.5 flex flex-wrap items-center gap-2 pl-8">
                  <span className="inline-flex items-center gap-2 rounded-md border px-2.5 py-1.5 text-[13px] dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100">
                    <FolderIcon size={14} />
                    <span className="truncate">{chosenPath}</span>
                  </span>
                  <button
                    type="button"
                    ref={changeRef}
                    onClick={() => setPickerOpen(true)}
                    className="rounded-md border px-2.5 py-1.5 text-[13px] hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
                  >
                    {t("placementChange")}
                  </button>
                </div>
              )}
            </div>
          ))}
        </fieldset>

        {/* Recording started from a calendar event: the only case where the meeting's end time is known, so
            the only case where the recorder can end a take by itself. */}
        <div className="overflow-hidden rounded-lg border dark:border-gray-700">
          <div className="flex items-start justify-between gap-5 px-4 py-3.5">
            <div className="min-w-0">
              <div className="flex items-center gap-2">
                <span className="shrink-0 text-gray-500 dark:text-gray-400">
                  <CalendarIcon size={14} />
                </span>
                <h3 className="text-[15px] font-semibold dark:text-gray-100">{t("calendarAutoStopHeading")}</h3>
              </div>
              <p className="mt-1 text-[13px] text-pretty text-gray-500 dark:text-gray-400">{t("calendarAutoStopBody")}</p>
            </div>
            {/* A native checkbox cannot be styled as a track and knob without hiding it, which loses the
                focus ring; `role="switch"` on a button is the same semantics with a real focusable target.
                The heading is its accessible name - the control has no visible label of its own. */}
            <button
              type="button"
              role="switch"
              aria-checked={calendarAutoStop}
              aria-label={t("calendarAutoStopHeading")}
              onClick={() => setCalendarAutoStop((on) => !on)}
              className={`relative h-6 w-11 shrink-0 rounded-full transition-colors ${
                calendarAutoStop ? "bg-blue-600" : "bg-gray-300 dark:bg-gray-700"
              }`}
            >
              <span
                aria-hidden
                className={`absolute top-[3px] h-[18px] w-[18px] rounded-full bg-white transition-[left] ${
                  calendarAutoStop ? "left-[23px]" : "left-[3px]"
                }`}
              />
            </button>
          </div>

          {/* Absent rather than disabled: the two durations say HOW a recording ends, and there is nothing
              for them to qualify while the switch is off. */}
          {calendarAutoStop && (
            <div className="flex flex-col gap-3 border-t bg-gray-50 px-4 py-3.5 dark:border-gray-700 dark:bg-white/[.02]">
              <div className="flex flex-wrap items-center gap-2 text-sm dark:text-gray-200">
                <span>{t("calendarStopPrefix")}</span>
                <input
                  type="number"
                  min={1}
                  step={1}
                  value={afterMinutes}
                  onChange={(e) => setAfterMinutes(e.target.value)}
                  aria-label={t("calendarAfterMinutes")}
                  className="w-[60px] rounded border px-2 py-1.5 text-center text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
                <span>{t("calendarStopMinutesSuffix")}</span>
              </div>
              <div className="flex flex-wrap items-center gap-2 text-sm dark:text-gray-200">
                <span>{t("calendarSilencePrefix")}</span>
                <input
                  type="number"
                  min={1}
                  step={1}
                  value={silenceSeconds}
                  onChange={(e) => setSilenceSeconds(e.target.value)}
                  aria-label={t("calendarSilenceSeconds")}
                  className="w-[60px] rounded border px-2 py-1.5 text-center text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
                <span>{t("calendarSilenceSuffix")}</span>
              </div>
              {/* The two per-field hints this replaces said what each number was for in the abstract; one
                  worked example says it once, in the reader's own numbers. */}
              <p
                aria-live="polite"
                className="border-l-2 border-blue-500/50 pl-3 text-[13px] text-gray-500 dark:text-gray-400"
              >
                {t("calendarAutoStopExample", {
                  until: clockAfter(11, positiveOr(afterMinutes, DEFAULT_AFTER_MINUTES)),
                  seconds: positiveOr(silenceSeconds, DEFAULT_SILENCE_SECONDS),
                })}
              </p>
            </div>
          )}
        </div>
      </div>

      {pickerOpen && (
        <FolderPickerModal
          sections={sections}
          selectedId={placementSectionId}
          onSelect={setPlacementSectionId}
          onClose={() => {
            setPickerOpen(false);
            changeRef.current?.focus();
          }}
        />
      )}
    </>
  );
}
