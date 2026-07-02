import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { useAuth } from "../auth";
import { useLanguage } from "../language";
import { fetchLanguages } from "../lib/languages";

/// Personal preferences for any user: display name, native language (the default translation target),
/// and the language the app UI is shown in. Saving re-issues the access token so the new name shows
/// immediately, and caches the UI language locally.
export default function PreferencesModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { setSession } = useAuth();
  const { available: uiLanguages, setLanguage } = useLanguage();
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const { data: languages } = useQuery({ queryKey: ["languages"], queryFn: fetchLanguages });

  const [fullName, setFullName] = useState("");
  const [nativeLanguage, setNativeLanguage] = useState("");
  const [uiLanguage, setUiLanguage] = useState("");
  const [gCalendar, setGCalendar] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (profile) {
      setFullName(profile.fullName ?? "");
      setNativeLanguage(profile.nativeLanguage ?? "");
      setUiLanguage(profile.uiLanguage ?? "");
      setGCalendar(profile.googleCalendar);
    }
  }, [profile]);

  async function connectGoogleData() {
    setError(null);
    try {
      // Full-page navigation to Google's consent screen; it returns to /?google=connected.
      window.location.assign(await api.connectGoogle({ calendar: gCalendar }));
    } catch (e) {
      setError(apiErrorMessage(e, t("googleConnectError")));
    }
  }

  async function disconnectGoogleData() {
    setError(null);
    try {
      await api.disconnectGoogle();
      setGCalendar(false);
      qc.invalidateQueries({ queryKey: ["user-profile"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("googleDisconnectError")));
    }
  }

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function onOk() {
    setError(null);
    setBusy(true);
    try {
      const res = await api.updateProfile({
        fullName: fullName.trim() || null,
        nativeLanguage: nativeLanguage || null,
        uiLanguage: uiLanguage || null,
      });
      setSession(res.accessToken); // adopt the fresh token so the new display name takes effect now
      if (uiLanguage) setLanguage(uiLanguage); // apply the UI language live (and persist it)
      qc.invalidateQueries({ queryKey: ["user-profile"] });
      onClose();
    } catch (e) {
      setError(apiErrorMessage(e, t("preferencesSaveError")));
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label="Preferences"
        className="flex max-h-[85vh] w-full max-w-md flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="border-b px-5 pt-4 pb-3 dark:border-gray-700">
          <h2 className="text-base font-semibold dark:text-gray-100">{t("preferencesTitle")}</h2>
        </div>

        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-5">
          <label className="block text-sm">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("displayName")}</span>
            <input
              name="diariz-display-name"
              autoComplete="off"
              value={fullName}
              onChange={(e) => setFullName(e.target.value)}
              placeholder={profile?.email ?? ""}
              className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>

          <label className="block text-sm">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("nativeLanguage")}</span>
            <select
              value={nativeLanguage}
              onChange={(e) => setNativeLanguage(e.target.value)}
              aria-label={t("nativeLanguage")}
              className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            >
              <option value="">{t("notSet")}</option>
              {languages?.map((l) => (
                <option key={l.code} value={l.code}>
                  {l.englishName} ({l.nativeName})
                </option>
              ))}
            </select>
            <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">{t("nativeLanguageHint")}</span>
          </label>

          <label className="block text-sm">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("appLanguage")}</span>
            <select
              value={uiLanguage}
              onChange={(e) => setUiLanguage(e.target.value)}
              aria-label={t("appLanguage")}
              className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            >
              <option value="">{t("followBrowser")}</option>
              {/* Only languages with a shipped UI catalog can be the interface language. */}
              {uiLanguages.map((l) => (
                <option key={l.code} value={l.code}>
                  {l.englishName} ({l.nativeName})
                </option>
              ))}
            </select>
            <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">{t("appLanguageHint")}</span>
          </label>

          <div className="border-t pt-3 dark:border-gray-700">
            <span className="mb-1 block text-sm text-gray-600 dark:text-gray-300">{t("googleAccount")}</span>
            {profile?.googleConnected ? (
              <div className="space-y-2 text-sm">
                <p className="text-gray-700 dark:text-gray-200">
                  <span className="mr-1.5 inline-block rounded bg-green-100 px-1.5 py-0.5 text-[10px] font-medium text-green-800 dark:bg-green-900/40 dark:text-green-300">
                    {t("googleConnectedBadge")}
                  </span>
                  {t("googleConnectedAs", { email: profile.email })}
                </p>
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={gCalendar} onChange={(e) => setGCalendar(e.target.checked)} />
                  <span className="dark:text-gray-200">{t("googleCalendarRead")}</span>
                </label>
                <p className="text-xs text-gray-400 dark:text-gray-500">{t("googleDataHint")}</p>
                <div className="flex gap-2">
                  <button
                    type="button"
                    onClick={connectGoogleData}
                    disabled={!gCalendar}
                    className="rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
                  >
                    {t("googleConnectData")}
                  </button>
                  {profile.googleCalendar && (
                    <button
                      type="button"
                      onClick={disconnectGoogleData}
                      className="rounded border px-2 py-1 text-xs text-red-600 hover:bg-red-50 dark:border-gray-700 dark:text-red-400 dark:hover:bg-gray-800"
                    >
                      {t("googleDisconnect")}
                    </button>
                  )}
                </div>
              </div>
            ) : (
              <p className="text-sm text-gray-500 dark:text-gray-400">{t("googleNotConnected")}</p>
            )}
          </div>
        </div>

        <div className="flex items-center justify-end gap-2 border-t px-5 py-3 dark:border-gray-700">
          {error && <p className="mr-auto text-sm text-red-600 dark:text-red-400">{error}</p>}
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="button"
            onClick={onOk}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? t("common:saving") : t("common:ok")}
          </button>
        </div>
      </div>
    </div>
  );
}
