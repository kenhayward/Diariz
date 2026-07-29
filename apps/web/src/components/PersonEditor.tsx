import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { Person } from "../lib/types";

/// Editor for one person in the directory. Follows the app's established shape: a local draft copied from
/// the server object, imperative validation in the save handler, and a single error banner - there is no
/// form library here and no schema validation.
///
/// Two permission rules are rendered deliberately differently, and the difference matters:
///
/// - **Opt-out is always shown**, with the person's real value, and merely *disabled* when the viewer may
///   not change it. Someone needs to be able to see that a person opted out even if they cannot alter it;
///   hiding it would make an opted-out person look like an ordinary one.
/// - **Erase voiceprint is hidden entirely** without the permission. An action you cannot perform should
///   not advertise itself.
///
/// Both read `person.canManageBiometrics`, which is the **server's** answer (ManagePeople, or it is you).
/// Do not recompute that rule here: the two would drift the first time either side is edited.
export default function PersonEditor({
  person,
  canManagePeople,
  onClose,
}: {
  person: Person;
  canManagePeople: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation("people");
  const qc = useQueryClient();

  const [name, setName] = useState(person.name);
  const [title, setTitle] = useState(person.title ?? "");
  const [company, setCompany] = useState(person.companyName ?? "");
  const [email, setEmail] = useState(person.email ?? "");
  const [phone, setPhone] = useState(person.phone ?? "");
  const [isInternal, setIsInternal] = useState(person.isInternal);
  const [optOut, setOptOut] = useState(person.voiceprintOptOut);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    setName(person.name);
    setTitle(person.title ?? "");
    setCompany(person.companyName ?? "");
    setEmail(person.email ?? "");
    setPhone(person.phone ?? "");
    setIsInternal(person.isInternal);
    setOptOut(person.voiceprintOptOut);
    setError(null);
    setSaved(false);
  }, [person]);

  const linked = person.linkedUserId != null;
  const field = "w-full rounded border px-2 py-1 text-sm disabled:bg-gray-100 disabled:text-gray-500 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100 dark:disabled:bg-gray-900";
  const labelSpan = "mb-1 block text-gray-600 dark:text-gray-300";

  async function act(fn: () => Promise<void>, fallback: string) {
    setError(null);
    setBusy(true);
    try {
      await fn();
      await qc.invalidateQueries({ queryKey: ["people"] });
    } catch (e) {
      setError(apiErrorMessage(e, t(fallback)));
    } finally {
      setBusy(false);
    }
  }

  async function save() {
    if (!name.trim()) return setError(t("errNameRequired"));
    if (email.trim() && !email.includes("@")) return setError(t("errEmailInvalid"));

    await act(async () => {
      await api.updatePerson(person.id, {
        // Name and email follow the account for a linked person; sending them would only 400.
        name: linked ? undefined : name.trim(),
        email: linked ? undefined : email.trim() || null,
        title: title.trim() || null,
        companyName: company.trim() || null,
        phone: phone.trim() || null,
        isInternal,
        voiceprintOptOut: optOut,
      });
      setSaved(true);
    }, "errSaveFailed");
  }

  /// Ticking this destroys a voiceprint, so it asks first and says exactly what goes. Unticking is not
  /// destructive and needs no confirmation - but it does not bring anything back either.
  function toggleOptOut(next: boolean) {
    // The disabled attribute is presentation; this is the guard. A programmatic click still reaches the
    // handler, and relying on the attribute alone would make the permission a styling detail.
    if (!person.canManageBiometrics) return;
    if (!next) return setOptOut(false);
    if (!window.confirm(t("confirmOptOut", { name: person.name }))) return;
    setOptOut(true);
  }

  return (
    <div className="flex h-full flex-col gap-3 overflow-y-auto p-4">
      <label className="block text-sm">
        <span className={labelSpan}>{t("fieldName")}</span>
        <input
          value={name}
          disabled={linked}
          onChange={(e) => setName(e.target.value)}
          aria-label={t("fieldName")}
          className={field}
        />
      </label>

      <div className="grid grid-cols-2 gap-3">
        <label className="block text-sm">
          <span className={labelSpan}>{t("fieldTitle")}</span>
          <input value={title} onChange={(e) => setTitle(e.target.value)} aria-label={t("fieldTitle")} className={field} />
        </label>
        <label className="block text-sm">
          <span className={labelSpan}>{t("fieldCompany")}</span>
          <input value={company} onChange={(e) => setCompany(e.target.value)} aria-label={t("fieldCompany")} className={field} />
        </label>
      </div>

      <div className="grid grid-cols-2 gap-3">
        <label className="block text-sm">
          <span className={labelSpan}>{t("fieldEmail")}</span>
          <input
            value={email}
            disabled={linked}
            onChange={(e) => setEmail(e.target.value)}
            aria-label={t("fieldEmail")}
            className={field}
          />
        </label>
        <label className="block text-sm">
          <span className={labelSpan}>{t("fieldPhone")}</span>
          <input value={phone} onChange={(e) => setPhone(e.target.value)} aria-label={t("fieldPhone")} className={field} />
        </label>
      </div>

      {linked && <p className="text-xs text-gray-500 dark:text-gray-400">{t("linkedFieldsLocked")}</p>}

      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={isInternal} onChange={(e) => setIsInternal(e.target.checked)} />
        <span className="text-gray-700 dark:text-gray-200">{t("fieldInternal")}</span>
      </label>

      <label className="flex items-start gap-2 text-sm">
        <input
          type="checkbox"
          checked={optOut}
          disabled={!person.canManageBiometrics}
          onChange={(e) => toggleOptOut(e.target.checked)}
          aria-label={t("fieldOptOut")}
          className="mt-0.5"
        />
        <span>
          <span className="text-gray-700 dark:text-gray-200">{t("fieldOptOut")}</span>
          <span className="mt-0.5 block text-xs text-gray-500 dark:text-gray-400">
            {person.canManageBiometrics
              ? person.isSelf
                ? t("optOutSelfHint")
                : t("optOutHint")
              : t("optOutLockedHint")}
          </span>
        </span>
      </label>

      <div className="mt-2 flex flex-wrap items-center gap-3 border-t pt-3 dark:border-gray-700">
        <button
          type="button"
          onClick={save}
          disabled={busy}
          className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
        >
          {busy ? t("common:saving") : t("common:save")}
        </button>
        <button type="button" onClick={onClose} className="text-sm text-gray-600 dark:text-gray-300">
          {t("common:cancel")}
        </button>

        {person.hasVoiceprint && person.canManageBiometrics && (
          <button
            type="button"
            onClick={() => {
              if (window.confirm(t("confirmEraseVoiceprint", { name: person.name }))) {
                void act(() => api.deleteVoiceprint(person.id), "errSaveFailed");
              }
            }}
            className="text-sm text-red-600 dark:text-red-400"
          >
            {t("eraseVoiceprint")}
          </button>
        )}

        {canManagePeople && (
          <button
            type="button"
            onClick={() => {
              if (window.confirm(t("confirmDelete", { name: person.name }))) {
                void act(async () => {
                  await api.deletePerson(person.id);
                  onClose();
                }, "errSaveFailed");
              }
            }}
            className="ml-auto text-sm text-red-600 dark:text-red-400"
          >
            {t("delete")}
          </button>
        )}

        {error && <span className="w-full text-sm text-red-600 dark:text-red-400">{error}</span>}
        {saved && <span className="text-sm text-green-600 dark:text-green-400">{t("common:saved")}</span>}
      </div>
    </div>
  );
}
