import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { useAuth } from "../auth";
import { fetchLanguages } from "../lib/languages";
import { setStoredLanguage } from "../lib/language";

/// Personal preferences for any user: display name, native language (the default translation target),
/// and the language the app UI is shown in. Saving re-issues the access token so the new name shows
/// immediately, and caches the UI language locally.
export default function PreferencesModal({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient();
  const { setSession } = useAuth();
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const { data: languages } = useQuery({ queryKey: ["languages"], queryFn: fetchLanguages });

  const [fullName, setFullName] = useState("");
  const [nativeLanguage, setNativeLanguage] = useState("");
  const [uiLanguage, setUiLanguage] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (profile) {
      setFullName(profile.fullName ?? "");
      setNativeLanguage(profile.nativeLanguage ?? "");
      setUiLanguage(profile.uiLanguage ?? "");
    }
  }, [profile]);

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
      setStoredLanguage(uiLanguage || null); // cache the UI language locally (used pre-login / by i18n)
      qc.invalidateQueries({ queryKey: ["user-profile"] });
      onClose();
    } catch (e) {
      setError(apiErrorMessage(e, "Could not save your preferences."));
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
          <h2 className="text-base font-semibold dark:text-gray-100">Preferences</h2>
        </div>

        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-5">
          <label className="block text-sm">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">Display name</span>
            <input
              name="diariz-display-name"
              autoComplete="off"
              value={fullName}
              onChange={(e) => setFullName(e.target.value)}
              placeholder={profile?.email ?? "Your name"}
              className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>

          <label className="block text-sm">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">Native language</span>
            <select
              value={nativeLanguage}
              onChange={(e) => setNativeLanguage(e.target.value)}
              aria-label="Native language"
              className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            >
              <option value="">— Not set —</option>
              {languages?.map((l) => (
                <option key={l.code} value={l.code}>
                  {l.englishName} ({l.nativeName})
                </option>
              ))}
            </select>
            <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">
              Used as the default language when translating a transcript.
            </span>
          </label>

          <label className="block text-sm">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">App language</span>
            <select
              value={uiLanguage}
              onChange={(e) => setUiLanguage(e.target.value)}
              aria-label="App language"
              className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            >
              <option value="">— Follow my browser —</option>
              {languages?.map((l) => (
                <option key={l.code} value={l.code}>
                  {l.englishName} ({l.nativeName})
                </option>
              ))}
            </select>
            <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">
              The language the interface is shown in (where a translation is available).
            </span>
          </label>
        </div>

        <div className="flex items-center justify-end gap-2 border-t px-5 py-3 dark:border-gray-700">
          {error && <p className="mr-auto text-sm text-red-600 dark:text-red-400">{error}</p>}
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onOk}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? "Saving…" : "OK"}
          </button>
        </div>
      </div>
    </div>
  );
}
