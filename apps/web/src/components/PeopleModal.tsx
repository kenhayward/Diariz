import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { useAuth } from "../auth";
import HelpButton from "./HelpButton";
import PersonEditor from "./PersonEditor";
import MergePeopleDialog from "./MergePeopleDialog";
import PersonIdentityLine from "./PersonIdentityLine";
import type { PersonDuplicateGroup } from "../lib/types";

/// The first four narrow the server query. `needsReview` cannot - the warnings come from two other
/// endpoints - so it filters what came back instead.
type Filter = "all" | "internal" | "external" | "hasVoiceprint" | "needsReview";

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
  // This sitting only, deliberately: the pair really might be the same person, and the odd-sounding
  // recording really might be them on a car phone. Hiding either forever is not a decision to take from
  // one click with no undo. State lives in the modal, so closing and reopening brings everything back.
  //
  // Keyed on the person rather than the duplicate group: the row carries both kinds of warning, and one
  // Dismiss covering both is what "stop nagging me about this person" means.
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

  // Whose training set contains a sample resembling none of their others. The endpoint omits healthy
  // people, so an empty list means "nothing to fix" rather than "nothing loaded" - which is why the banner
  // disappears entirely instead of rendering an empty one.
  const { data: unhealthy = [] } = useQuery({
    queryKey: ["people-diagnostics"],
    queryFn: () => api.getDirectoryDiagnostics(),
    enabled: permissions.managePeople,
  });

  const { data: duplicates = [] } = useQuery({
    queryKey: ["people-duplicates"],
    queryFn: () => api.findPersonDuplicates(),
    enabled: permissions.managePeople,
  });

  const selected = people.find((p) => p.id === selectedId) ?? null;

  // What each row has to say about itself. Both used to be panels above the directory; with both showing
  // they pushed the person card almost off screen, which is the thing they were asking you to look at.
  const healthFor = new Map(unhealthy.map((h) => [h.personId, h]));
  const duplicateFor = new Map<string, PersonDuplicateGroup>();
  for (const g of duplicates) for (const p of g.people) duplicateFor.set(p.id, g);

  const warned = (id: string) =>
    !dismissed.has(id) && (healthFor.has(id) || duplicateFor.has(id));

  const visiblePeople = filter === "needsReview" ? people.filter((p) => warned(p.id)) : people;

  // The merge follows the person you opened, rather than sitting above the directory.
  const selectedDuplicate =
    selected && !dismissed.has(selected.id) ? duplicateFor.get(selected.id) : undefined;

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
              {chip("needsReview", t("people:filterNeedsReview"))}
            </div>

            {(error || isError) && (
              <p className="shrink-0 text-sm text-red-600 dark:text-red-400">
                {error ?? t("people:errLoadFailed")}
              </p>
            )}

            <div className="flex min-h-0 flex-1 gap-4">
              {/* The only scrolling region. */}
              <ul className="w-80 shrink-0 overflow-y-auto rounded border dark:border-gray-700">
                {visiblePeople.length === 0 && (
                  <li className="p-3 text-sm text-gray-500 dark:text-gray-400">{t("people:empty")}</li>
                )}
                {visiblePeople.map((p) => {
                  const health = warned(p.id) ? healthFor.get(p.id) : undefined;
                  const duplicate = warned(p.id) ? duplicateFor.get(p.id) : undefined;
                  return (
                    // A flex row rather than one big button, because Dismiss has to be a real button and a
                    // button cannot be nested inside another one.
                    <li key={p.id} className="flex items-start">
                      <button
                        type="button"
                        onClick={() => setSelectedId(p.id)}
                        aria-pressed={p.id === selectedId}
                        // Name over account identity, with the voiceprint marker keeping its place at the
                        // end. The second line is what makes two people of the same name tellable apart;
                        // both lines truncate so a long directory still stays scannable.
                        className={`flex min-w-0 flex-1 items-center gap-2 px-3 py-1.5 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800 ${
                          p.id === selectedId ? "bg-gray-100 dark:bg-gray-800" : ""
                        }`}
                      >
                        <span className="min-w-0 flex-1">
                          {/* truncate needs a block: on an inline span it sets only white-space:nowrap,
                              so the text would neither wrap nor ellipsise and would overflow the row. */}
                          <span className="block truncate text-gray-800 dark:text-gray-100">{p.name}</span>
                          <PersonIdentityLine
                            person={p}
                            className="block truncate text-xs text-gray-500 dark:text-gray-400"
                          />
                          {/* Only rendered when there is something to say, so an ordinary directory stays
                              two lines a row and the amber means something when it appears. */}
                          {(health || duplicate) && (
                            <span className="block truncate text-xs text-amber-700 dark:text-amber-400">
                              {health &&
                                t("people:warnVoiceprint", {
                                  count: health.aloneCount,
                                  total: health.sampleCount,
                                })}
                              {health && duplicate ? " - " : ""}
                              {duplicate && t("people:warnDuplicate")}
                            </span>
                          )}
                        </span>
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
                      {(health || duplicate) && (
                        <button
                          type="button"
                          onClick={() => setDismissed((cur) => new Set(cur).add(p.id))}
                          title={t("people:dismissWarningsHint")}
                          className="shrink-0 px-2 py-1.5 text-xs text-amber-700 underline dark:text-amber-400"
                        >
                          {t("people:dismissWarnings")}
                        </button>
                      )}
                    </li>
                  );
                })}
              </ul>

              <div className="flex min-w-0 flex-1 flex-col gap-2">
                {/* Follows the person you opened. As a panel above the directory it covered the very card
                    it was asking you to look at, which is what made it worth moving. */}
                {selected && selectedDuplicate && (
                  <div className="shrink-0 rounded border border-amber-300 bg-amber-50 p-2 text-xs dark:border-amber-700 dark:bg-amber-950">
                    <div className="flex flex-wrap items-start gap-2">
                      <div className="min-w-0 flex-1 text-amber-900 dark:text-amber-200">
                        {selectedDuplicate.reason === "email"
                          ? t("people:duplicatesReasonEmail")
                          : t("people:duplicatesReasonName")}
                        :
                        {/* One line per person with its account identity. Joining bare names produced two
                            identical names, which cannot be decided on - and the pair most worth reporting
                            is exactly the one where the names match. */}
                        <ul className="mt-0.5 space-y-0.5">
                          {selectedDuplicate.people.map((dp) => (
                            <li key={dp.id} className="truncate">
                              {dp.name}{" "}
                              <PersonIdentityLine
                                person={dp}
                                className="text-amber-800 dark:text-amber-300"
                              />
                            </li>
                          ))}
                        </ul>
                      </div>
                      {selectedDuplicate.people.length > 1 && (
                        <button
                          type="button"
                          onClick={() => setMerging(selectedDuplicate)}
                          className="rounded border border-amber-400 px-2 py-0.5 text-amber-900 dark:border-amber-600 dark:text-amber-200"
                        >
                          {t("people:mergeReview")}
                        </button>
                      )}
                    </div>
                  </div>
                )}

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
