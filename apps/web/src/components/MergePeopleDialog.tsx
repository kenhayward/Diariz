import { useState } from "react";
import { useTranslation } from "react-i18next";
import HelpButton from "./HelpButton";
import type { Person } from "../lib/types";

/// Confirms a merge by describing what it will actually do to *these two records*.
///
/// A merge deletes one row and has no undo, and in a shared directory it changes what everyone else sees. A
/// one-line `window.confirm` naming the two people was not enough to decide with: it said nothing about which
/// record survives, what moves, or what is lost. The consequences differ per pair - a voiceprint may travel,
/// contact details may be picked up, an account link may transfer - so the explanation has to be computed
/// from the pair rather than written once as static copy.
///
/// The direction is the decision that matters, since the survivor keeps its own values and only fills gaps
/// from the other. So it can be swapped here, and everything the dialog says changes with it.
export default function MergePeopleDialog({
  people,
  reason,
  onMerge,
  onClose,
}: {
  people: [Person, Person];
  reason: string;
  onMerge: (targetId: string, sourceId: string) => Promise<void>;
  onClose: () => void;
}) {
  const { t } = useTranslation(["people", "common"]);
  const [flipped, setFlipped] = useState(false);
  const [busy, setBusy] = useState(false);

  const [target, source] = flipped ? [people[1], people[0]] : [people[0], people[1]];

  // The server refuses this with a 400, and rightly: two accounts are two humans. Say so before someone
  // commits to an action they have been told cannot be undone.
  const bothLinked = target.linkedUserId !== null && source.linkedUserId !== null;

  // Only the fields the survivor is missing and the other record can supply. Anything the survivor already
  // has is untouched, so listing it would be noise at best and misleading at worst.
  const blank = (v: string | null) => !v || v.trim() === "";
  const gains = (
    [
      ["fieldEmail", target.email, source.email],
      ["fieldTitle", target.title, source.title],
      ["fieldCompany", target.companyName, source.companyName],
      ["fieldPhone", target.phone, source.phone],
    ] as const
  )
    .filter(([, keep, salvage]) => blank(keep) && !blank(salvage))
    .map(([key]) => t(`people:${key}`));

  const movingSamples = source.sampleCount;
  const linkMoves = target.linkedUserId === null && source.linkedUserId !== null;

  async function confirm() {
    setBusy(true);
    try {
      await onMerge(target.id, source.id);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-label={t("people:mergeTitle")}
        className="w-full max-w-lg rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <h3 className="flex items-center gap-1.5 text-base font-semibold dark:text-gray-100">
          {t("people:mergeTitle")}
          <HelpButton topic="merging-people" />
        </h3>

        <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">
          {reason === "email" ? t("people:duplicatesReasonEmail") : t("people:duplicatesReasonName")}
        </p>

        <div className="mt-4 space-y-1 rounded border p-3 text-sm dark:border-gray-700">
          <p className="font-medium text-gray-900 dark:text-gray-50">
            {t("people:mergeKeeps", { name: target.name })}
          </p>
          <p className="text-gray-500 line-through dark:text-gray-400">
            {t("people:mergeDeletes", { name: source.name })}
          </p>
          <button
            type="button"
            onClick={() => setFlipped((f) => !f)}
            className="mt-1 text-xs underline text-gray-600 dark:text-gray-300"
          >
            {t("people:mergeSwap")}
          </button>
        </div>

        {bothLinked ? (
          <p className="mt-4 rounded border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-700 dark:bg-amber-950 dark:text-amber-200">
            {t("people:mergeBlockedBothAccounts")}
          </p>
        ) : (
          <ul
            data-testid="merge-gains"
            className="mt-4 list-disc space-y-1 pl-5 text-sm text-gray-700 dark:text-gray-300"
          >
            {movingSamples > 0 && (
              <li>
                {t("people:mergeEffectSamples", {
                  samples: t("people:sampleCount", { count: movingSamples }),
                  name: target.name,
                })}
              </li>
            )}
            {gains.length > 0 && (
              <li>{t("people:mergeEffectGains", { name: target.name, fields: gains.join(", ") })}</li>
            )}
            {linkMoves && <li>{t("people:mergeEffectAccount", { name: target.name })}</li>}
            <li>{t("people:mergeEffectRelabel", { source: source.name, target: target.name })}</li>
            <li>{t("people:mergeEffectFinal", { name: source.name })}</li>
          </ul>
        )}

        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm dark:border-gray-700 dark:text-gray-200"
          >
            {t("common:cancel")}
          </button>
          {!bothLinked && (
            <button
              type="button"
              onClick={confirm}
              disabled={busy}
              className="rounded bg-red-600 px-3 py-1.5 text-sm text-white disabled:opacity-50"
            >
              {t("people:mergeConfirm")}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
