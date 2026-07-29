import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { useAuth } from "../auth";
import HelpButton from "../components/HelpButton";
import PersonEditor from "../components/PersonEditor";
import type { Person } from "../lib/types";

type Filter = "all" | "internal" | "external" | "hasVoiceprint";

/// The people directory: a master-detail page rather than a Preferences tab, because a platform-wide
/// directory is not a personal setting.
///
/// Reaching this page needs the Manage people permission - the same gate as the endpoint it reads. Anyone
/// can still find a person to label a speaker; that goes through the ungated search endpoint instead.
export default function People() {
  const { t } = useTranslation(["people", "common"]);
  const { permissions } = useAuth();
  const qc = useQueryClient();

  const [q, setQ] = useState("");
  const [filter, setFilter] = useState<Filter>("all");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

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

  if (!permissions.managePeople) {
    return <p className="p-6 text-sm text-gray-500 dark:text-gray-400">{t("people:optOutLockedHint")}</p>;
  }

  async function merge(target: Person, source: Person) {
    if (!window.confirm(t("people:confirmMerge", { source: source.name, target: target.name }))) return;
    setError(null);
    try {
      await api.mergePeople(target.id, source.id);
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["people"] }),
        qc.invalidateQueries({ queryKey: ["people-duplicates"] }),
      ]);
    } catch (e) {
      setError(apiErrorMessage(e, t("people:errSaveFailed")));
    }
  }

  const chip = (value: Filter, label: string) => (
    <button
      key={value}
      type="button"
      onClick={() => setFilter(value)}
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
    <div className="flex h-full flex-col gap-3 p-4">
      <div>
        <h2 className="flex items-center gap-1 text-lg font-semibold text-gray-900 dark:text-gray-100">
          {t("people:pageTitle")}
          <HelpButton topic="people-directory" />
        </h2>
        <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">{t("people:pageDescription")}</p>
      </div>

      {duplicates.length > 0 && (
        <div className="rounded border border-amber-300 bg-amber-50 p-3 text-sm dark:border-amber-700 dark:bg-amber-950">
          <p className="font-medium text-amber-900 dark:text-amber-200">{t("people:duplicatesHeading")}</p>
          <p className="mt-0.5 text-xs text-amber-800 dark:text-amber-300">{t("people:duplicatesHint")}</p>
          <ul className="mt-2 space-y-1">
            {duplicates.map((group, i) => (
              <li key={i} className="flex flex-wrap items-center gap-2 text-xs">
                <span className="text-gray-600 dark:text-gray-300">
                  {group.reason === "email" ? t("people:duplicatesReasonEmail") : t("people:duplicatesReasonName")}:{" "}
                  {group.people.map((p) => p.name).join(", ")}
                </span>
                {group.people.length > 1 && (
                  <button
                    type="button"
                    onClick={() => merge(group.people[0], group.people[1])}
                    className="rounded border border-amber-400 px-2 py-0.5 text-amber-900 dark:border-amber-600 dark:text-amber-200"
                  >
                    {t("people:mergeDuplicate", { name: group.people[0].name })}
                  </button>
                )}
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <input
          type="search"
          value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder={t("people:searchPlaceholder")}
          aria-label={t("people:searchPlaceholder")}
          className="w-64 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        {chip("all", t("people:filterAll"))}
        {chip("internal", t("people:filterInternal"))}
        {chip("external", t("people:filterExternal"))}
        {chip("hasVoiceprint", t("people:filterHasVoiceprint"))}
      </div>

      {(error || isError) && (
        <p className="text-sm text-red-600 dark:text-red-400">{error ?? t("people:errLoadFailed")}</p>
      )}

      <div className="flex min-h-0 flex-1 gap-4">
        <ul className="w-72 shrink-0 overflow-y-auto rounded border dark:border-gray-700">
          {people.length === 0 && (
            <li className="p-3 text-sm text-gray-500 dark:text-gray-400">{t("people:empty")}</li>
          )}
          {people.map((p) => (
            <li key={p.id}>
              <button
                type="button"
                onClick={() => setSelectedId(p.id)}
                aria-pressed={p.id === selectedId}
                className={`block w-full px-3 py-2 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800 ${
                  p.id === selectedId ? "bg-gray-100 dark:bg-gray-800" : ""
                }`}
              >
                <span className="block truncate text-gray-800 dark:text-gray-100">{p.name}</span>
                <span className="block truncate text-xs text-gray-500 dark:text-gray-400">
                  {[p.title, p.companyName].filter(Boolean).join(", ") ||
                    (p.hasVoiceprint ? t("people:hasVoiceprint") : t("people:noVoiceprint"))}
                </span>
              </button>
            </li>
          ))}
        </ul>

        <div className="min-w-0 flex-1 rounded border dark:border-gray-700">
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
  );
}
