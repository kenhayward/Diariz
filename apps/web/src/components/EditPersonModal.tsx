import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import { useAuth } from "../auth";
import PersonEditor from "./PersonEditor";

/// One person's details, opened from the speaker they were identified as.
///
/// The directory modal answers "who is in this organisation?"; this answers "this speaker's details are
/// wrong, fix them" - which is the question you have while reading a transcript, and it should not cost you a
/// trip through a list of everyone. It is the same `PersonEditor` the directory uses, on purpose: a second
/// editor would be a second set of validation rules to keep in step.
///
/// **It does not close on a backdrop click or on Escape.** Every other modal here does. Half-typed edits are
/// easy to lose by a stray click and impossible to recover, so the only ways out are Save and Close.
export default function EditPersonModal({
  personId,
  onClose,
}: {
  personId: string;
  onClose: () => void;
}) {
  const { t } = useTranslation(["people", "common"]);
  const { permissions } = useAuth();

  const { data, isError, isLoading } = useQuery({
    queryKey: ["person", personId],
    queryFn: () => api.getPerson(personId),
  });

  return (
    <div
      data-testid="edit-person-backdrop"
      className="fixed inset-0 z-[60] flex items-center justify-center bg-black/40 p-4"
    >
      <div
        role="dialog"
        aria-label={t("people:editPersonTitle")}
        // Stops a click that started inside the panel from ever being read as a backdrop click.
        onClick={(e) => e.stopPropagation()}
        className="w-full max-w-lg overflow-hidden rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        {isLoading && (
          <p className="p-6 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>
        )}
        {isError && (
          <div className="p-6">
            <p className="text-sm text-red-600 dark:text-red-400">{t("people:errLoadFailed")}</p>
            <button
              type="button"
              onClick={onClose}
              className="mt-3 rounded border px-3 py-1.5 text-sm dark:border-gray-700 dark:text-gray-200"
            >
              {t("common:close")}
            </button>
          </div>
        )}
        {data && (
          <PersonEditor
            person={data.person}
            canManagePeople={permissions.managePeople}
            onClose={onClose}
          />
        )}
      </div>
    </div>
  );
}
