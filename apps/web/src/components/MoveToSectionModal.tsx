import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { sectionCreateTarget } from "../lib/drillView";
import FolderPicker from "./FolderPicker";

/// `FolderPicker.selectedId` only distinguishes "root" (`null`) from "some folder" (an id) - there is no
/// third value for "unknown". This modal's own `currentSectionId` prop *does* have that third state
/// (`undefined`, when the caller does not know the recording's current section), so an id that can never
/// collide with a real section id stands in for it, keeping the picker from wrongly marking the root as
/// current.
const UNKNOWN_SECTION = " unknown ";

/// Move a recording into an existing section, ungroup it, or create a new section and move into it.
export default function MoveToSectionModal({
  recordingId,
  currentSectionId,
  roomId,
  onClose,
}: {
  recordingId: string;
  currentSectionId?: string | null; // undefined = unknown (mark nothing)
  /// The room whose folder structure this moves within - a shared room, or undefined for the personal room.
  roomId?: string | null;
  onClose: () => void;
}) {
  const { t } = useTranslation("workspace");
  const qc = useQueryClient();
  const { data: sections = [] } = useQuery({
    queryKey: ["sections", roomId ?? null],
    queryFn: () => api.listSections(roomId),
  });
  const [newName, setNewName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Follows FolderPicker's own drill position (it stays uncontrolled - this is just a report) so
  // create-and-move lands where the user is looking instead of always at the top level. `blocked` covers
  // both the 8-level depth cap and a drilled id that has fallen out of the tree (see `sectionCreateTarget`).
  const [drillId, setDrillId] = useState<string | null>(null);
  const createTarget = sectionCreateTarget(sections, drillId);
  const createParentId = createTarget.kind === "child" ? createTarget.parent.id : null;
  const createBlocked = createTarget.kind === "blocked";
  const createPlaceholder = createBlocked
    ? t("newSectionNestCapped")
    : createTarget.kind === "child"
      ? t("newSubSectionPlaceholder", { parent: createTarget.parent.name })
      : t("newSectionPlaceholder");

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function move(sectionId: string | null) {
    setBusy(true);
    setError(null);
    try {
      await api.moveRecording(recordingId, sectionId, roomId);
      qc.invalidateQueries({ queryKey: ["recordings"] });
      // The detail page's folder breadcrumbs come from the recording's own query, which is a different key
      // from the list - without this the page you moved it from keeps showing the old folder.
      qc.invalidateQueries({ queryKey: ["recording", recordingId] });
      onClose();
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  async function createAndMove() {
    const name = newName.trim();
    if (!name || createBlocked) return;
    setBusy(true);
    setError(null);
    try {
      const section = await api.createSection(name, createParentId, roomId);
      await api.moveRecording(recordingId, section.id, roomId);
      qc.invalidateQueries({ queryKey: ["recordings"] });
      qc.invalidateQueries({ queryKey: ["sections"] });
      qc.invalidateQueries({ queryKey: ["recording", recordingId] });
      onClose();
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("moveToSectionTitle")}
        className="w-full max-w-sm rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-2 text-base font-semibold dark:text-gray-100">{t("moveToSectionTitle")}</h2>

        <FolderPicker
          sections={sections}
          selectedId={currentSectionId === undefined ? UNKNOWN_SECTION : currentSectionId}
          onSelect={(id) => {
            if (!busy) move(id);
          }}
          onDrillChange={setDrillId}
        />

        <form
          className="mt-3 flex items-center gap-2 border-t pt-3 dark:border-gray-700"
          onSubmit={(e) => {
            e.preventDefault();
            createAndMove();
          }}
        >
          <input
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            placeholder={createPlaceholder}
            aria-label={createPlaceholder}
            disabled={busy || createBlocked}
            className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <button
            type="submit"
            disabled={busy || createBlocked || !newName.trim()}
            className="shrink-0 rounded border px-2 py-1 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("createAndMove")}
          </button>
        </form>

        {error && <p className="mt-2 text-sm text-red-600 dark:text-red-400">{error}</p>}
      </div>
    </div>
  );
}
