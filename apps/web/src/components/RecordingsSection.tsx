import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { RecordingPlacementMode } from "../lib/types";
import FolderPicker from "./FolderPicker";

/// Recordings tab: where a new recording lands in the user's Personal room (Ungrouped / the selected folder /
/// a fixed folder). Self-contained; its Save PUTs only the placement fields (tri-state), leaving the other
/// personal preferences untouched.
export default function RecordingsSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });
  // Personal-room folders for the "Use a specific folder" chooser (flattened "Parent › Child").
  const { data: sections = [] } = useQuery({ queryKey: ["sections"], queryFn: () => api.listSections() });

  const [placementMode, setPlacementMode] = useState<RecordingPlacementMode>("SelectedFolder");
  const [placementSectionId, setPlacementSectionId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (data) {
      setPlacementMode(data.placementMode ?? "SelectedFolder");
      setPlacementSectionId(data.placementSectionId ?? null);
    }
  }, [data]);

  // Render only once the settings have loaded, so an early interaction can't be overwritten by the arriving
  // initial values (the effect above seeds state from `data`).
  if (!data) return null;

  async function onSave() {
    setError(null);
    setSaved(false);
    setBusy(true);
    try {
      await api.updateUserSettings({
        placementMode,
        placementSectionId: placementMode === "SpecificFolder" ? placementSectionId : null,
      });
      qc.invalidateQueries({ queryKey: ["user-settings"] });
      setSaved(true);
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

      <div className="flex items-center gap-3 border-t pt-3 dark:border-gray-700">
        <button
          type="button"
          onClick={onSave}
          disabled={busy}
          className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
        >
          {busy ? t("common:saving") : t("common:save")}
        </button>
        {error && <span className="text-sm text-red-600 dark:text-red-400">{error}</span>}
        {saved && !error && <span className="text-sm text-green-600 dark:text-green-400">{t("profileSaved")}</span>}
      </div>
    </div>
  );
}
