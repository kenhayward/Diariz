import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { useAuth } from "../auth";
import HelpButton from "./HelpButton";
import PersonEditor from "./PersonEditor";
import MergePeopleDialog from "./MergePeopleDialog";
import type { PersonDuplicateGroup } from "../lib/types";

type Filter = "all" | "internal" | "external" | "hasVoiceprint";

/// The people directory, as a modal rather than a route.
///
/// A modal because the directory is almost always consulted *while reading a transcript* - "who is this
/// speaker?" - and a route would throw that context away. It matches Meeting Types and the other account-menu
/// screens, so the shell here is deliberately the same shape as ManageMeetingTypesModal.
///
/// Only the list scrolls. The editor is a fixed panel beside it, so typing into a field never moves the
/// field, and a long directory does not push the form off screen.
export default function PeopleModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation(["people", "common"]);
  const { permissions } = useAuth();
  const qc = useQueryClient();

  const [q, setQ] = useState("");
  const [filter, setFilter] = useState<Filter>("all");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [merging, setMerging] = useState<PersonDuplicateGroup | null>(null);
  // This sitting only, deliberately: the pair really might be the same person, and hiding a suggestion
  // forever is not a decision to take from a banner with one click and no undo. State lives in the modal,
  // so closing and reopening brings everything back.
  const [dismissed, setDismissed] = useState<Set<string>>(new Set());

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
    }
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  /// The directory can change anything a recording shows about a speaker: their name, job title, company,
  /// internal-or-external marker, or the person record itself through a delete or a merge. The Speakers panel
  /// renders those from a snapshot inside the recording payload, so an open recording behind this modal keeps
  /// the old details and a correct edit reads as one that did not save.
  ///
  /// On unmount, not in `onClose`: every exit - the cross, the footer button, Escape - funnels through
  /// unmount, and so will any exit added later. One hook here rather than an `onSaved` per mutation, because
  /// merge lives in this component and delete and erase-voiceprint live in the editor, and a fifth mutation
  /// added later would silently join whichever ones forgot.
  ///
  /// The `["recording"]` prefix, not one id: this modal does not know which recording is behind it, and it
  /// opens from the account menu as well as a recording's Speakers toolbar. React Query only refetches
  /// *active* queries, so with no recording open this costs nothing.
  useEffect(() => {
    return () => void qc.invalidateQueries({ queryKey: ["recording"] });
  }, [qc]);

  const params = {
    q: q.trim() || undefined,
    isInternal: filter === "internal" ? true : filter === "external" ? false : undefined,
    hasVoiceprint: filter === "hasVoiceprint" ? true : undefined,
  };

  const { data: people = [], isError } = useQuery({
    queryKey: ["people", params],
    queryFn: () => api.listPeople(params),
    enabled: permissions.managePeople,
  });

  const { data: duplicates = [] } = useQuery({
    queryKey: ["people-duplicates"],
    queryFn: () => api.findPersonDuplicates(),
    enabled: permissions.managePeople,
  });

  const selected = people.find((p) => p.id === selectedId) ?? null;

  // Identity is the people in the group, not its position: a merge elsewhere reorders the list, and a
  // dismissal keyed on the index would then hide the wrong pair.
  const groupKey = (g: PersonDuplicateGroup) =>
    `${g.reason}:${g.people.map((p) => p.id).sort().join(",")}`;
  const visibleDuplicates = duplicates.filter((g) => !dismissed.has(groupKey(g)));

  async function merge(targetId: string, sourceId: string) {
    setError(null);
    try {
      await api.mergePeople(targetId, sourceId);
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["people"] }),
        qc.invalidateQueries({ queryKey: ["people-duplicates"] }),
      ]);
      setMerging(null);
    } catch (e) {
      setError(apiErrorMessage(e, t("people:errSaveFailed")));
      setMerging(null);
    }
  }

  const chip = (value: Filter, label: string) => (
    <button
      key={value}
      type="button"
      onClick={() => setFilter(value)}
      aria-pressed={filter === value}
      className={`rounded-full border px-3 py-1 text-xs ${
        filter === value
          ? "border-gray-900 bg-gray-900 text-white dark:border-gray-100 dark:bg-gray-100 dark:text-gray-900"
          : "border-gray-300 text-gray-600 dark:border-gray-700 dark:text-gray-300"
      }`}
    >
      {label}
    </button>
  );

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-label={t("people:pageTitle")}
        className="flex h-[88vh] w-[92vw] max-w-7xl flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-center justify-between border-b px-5 py-3 dark:border-gray-700">
          <h2 className="flex items-center gap-1.5 text-base font-semibold dark:text-gray-100">
            {t("people:pageTitle")}
            <HelpButton topic="people-directory" />
          </h2>
          <button
            type="button"
            aria-label={t("common:close")}
            onClick={onClose}
            className="rounded p-1 text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
          >
            ✕
          </button>
        </div>

        {!permissions.managePeople ? (
          <p className="p-6 text-sm text-gray-500 dark:text-gray-400">{t("people:optOutLockedHint")}</p>
        ) : (
          <div className="flex min-h-0 flex-1 flex-col gap-3 p-4">
            {visibleDuplicates.length > 0 && (
              <div className="shrink-0 rounded border border-amber-300 bg-amber-50 p-3 text-sm dark:border-amber-700 dark:bg-amber-950">
                <p className="font-medium text-amber-900 dark:text-amber-200">{t("people:duplicatesHeading")}</p>
                <p className="mt-0.5 text-xs text-amber-800 dark:text-amber-300">{t("people:duplicatesHint")}</p>
                <ul className="mt-2 space-y-1">
                  {visibleDuplicates.map((group, i) => (
                    <li key={i} className="flex flex-wrap items-center gap-2 text-xs">
                      <span className="text-gray-600 dark:text-gray-300">
                        {group.reason === "email"
                          ? t("people:duplicatesReasonEmail")
                          : t("people:duplicatesReasonName")}
                        : {group.people.map((p) => p.name).join(", ")}
                      </span>
                      {group.people.length > 1 && (
                        <button
                          type="button"
                          onClick={() => setMerging(group)}
                          className="rounded border border-amber-400 px-2 py-0.5 text-amber-900 dark:border-amber-600 dark:text-amber-200"
                        >
                          {t("people:mergeReview")}
                        </button>
                      )}
                      <button
                        type="button"
                        onClick={() =>
                          setDismissed((cur) => new Set(cur).add(groupKey(group)))
                        }
                        title={t("people:dismissDuplicateHint")}
                        className="text-amber-800 underline dark:text-amber-300"
                      >
                        {t("people:dismissDuplicate")}
                      </button>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            <div className="flex shrink-0 flex-wrap items-center gap-2">
              <input
                type="search"
                value={q}
                onChange={(e) => setQ(e.target.value)}
                placeholder={t("people:searchPlaceholder")}
                aria-label={t("people:searchPlaceholder")}
                className="w-72 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
              />
              {chip("all", t("people:filterAll"))}
              {chip("internal", t("people:filterInternal"))}
              {chip("external", t("people:filterExternal"))}
              {chip("hasVoiceprint", t("people:filterHasVoiceprint"))}
            </div>

            {(error || isError) && (
              <p className="shrink-0 text-sm text-red-600 dark:text-red-400">
                {error ?? t("people:errLoadFailed")}
              </p>
            )}

            <div className="flex min-h-0 flex-1 gap-4">
              {/* The only scrolling region. */}
              <ul className="w-80 shrink-0 overflow-y-auto rounded border dark:border-gray-700">
                {people.length === 0 && (
                  <li className="p-3 text-sm text-gray-500 dark:text-gray-400">{t("people:empty")}</li>
                )}
                {people.map((p) => (
                  <li key={p.id}>
                    <button
                      type="button"
                      onClick={() => setSelectedId(p.id)}
                      aria-pressed={p.id === selectedId}
                      // One line per person: the name truncates and the voiceprint marker keeps its place
                      // at the end, so a long list stays scannable.
                      className={`flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800 ${
                        p.id === selectedId ? "bg-gray-100 dark:bg-gray-800" : ""
                      }`}
                    >
                      <span className="min-w-0 flex-1 truncate text-gray-800 dark:text-gray-100">{p.name}</span>
                      <span
                        title={p.hasVoiceprint ? t("people:hasVoiceprint") : t("people:noVoiceprint")}
                        aria-label={p.hasVoiceprint ? t("people:hasVoiceprint") : t("people:noVoiceprint")}
                        className={`shrink-0 rounded px-1 text-[10px] font-medium ${
                          p.hasVoiceprint
                            ? "bg-blue-100 text-blue-700 dark:bg-blue-900/40 dark:text-blue-300"
                            : "text-gray-300 dark:text-gray-600"
                        }`}
                      >
                        {p.hasVoiceprint ? t("people:voiceprintShort") : "-"}
                      </span>
                    </button>
                  </li>
                ))}
              </ul>

              {/* Fixed: the form never moves while the list beside it scrolls. */}
              <div className="min-w-0 flex-1 overflow-hidden rounded border dark:border-gray-700">
                {selected ? (
                  <PersonEditor
                    key={selected.id}
                    person={selected}
                    canManagePeople={permissions.managePeople}
                    onClose={() => setSelectedId(null)}
                  />
                ) : (
                  <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("people:noneSelected")}</p>
                )}
              </div>
            </div>
          </div>
        )}

        {merging && (
          <MergePeopleDialog
            people={[merging.people[0], merging.people[1]]}
            reason={merging.reason}
            onMerge={merge}
            onClose={() => setMerging(null)}
          />
        )}

        <div className="flex justify-end border-t px-5 py-3 dark:border-gray-700">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm dark:border-gray-700 dark:text-gray-200"
          >
            {t("common:close")}
          </button>
        </div>
      </div>
    </div>
  );
}
