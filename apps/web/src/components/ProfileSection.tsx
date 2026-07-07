import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { useAuth } from "../auth";
import { useLanguage } from "../language";
import { useTheme } from "../theme";
import { fetchLanguages } from "../lib/languages";
import type { ThemeChoice } from "../lib/theme";

const THEMES: { value: ThemeChoice; key: string }[] = [
  { value: "auto", key: "themeAuto" },
  { value: "light", key: "themeLight" },
  { value: "dark", key: "themeDark" },
];

/// Profile tab: display name, native/UI language, free-text profile fields, and the colour theme.
/// Self-contained: its own Save persists everything (and re-issues the token for the new display name).
export default function ProfileSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { setSession } = useAuth();
  const { available: uiLanguages, setLanguage } = useLanguage();
  const { theme, setTheme } = useTheme();
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const { data: languages } = useQuery({ queryKey: ["languages"], queryFn: fetchLanguages });

  const [fullName, setFullName] = useState("");
  const [nativeLanguage, setNativeLanguage] = useState("");
  const [uiLanguage, setUiLanguage] = useState("");
  const [jobTitle, setJobTitle] = useState("");
  const [companyName, setCompanyName] = useState("");
  const [jobDescription, setJobDescription] = useState("");
  const [companyDescription, setCompanyDescription] = useState("");
  const [linkedIn, setLinkedIn] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (profile) {
      setFullName(profile.fullName ?? "");
      setNativeLanguage(profile.nativeLanguage ?? "");
      setUiLanguage(profile.uiLanguage ?? "");
      setJobTitle(profile.jobTitle ?? "");
      setCompanyName(profile.companyName ?? "");
      setJobDescription(profile.jobDescription ?? "");
      setCompanyDescription(profile.companyDescription ?? "");
      setLinkedIn(profile.linkedIn ?? "");
    }
  }, [profile]);

  async function onSave() {
    setError(null);
    setSaved(false);
    setBusy(true);
    try {
      const res = await api.updateProfile({
        fullName: fullName.trim() || null,
        nativeLanguage: nativeLanguage || null,
        uiLanguage: uiLanguage || null,
        jobTitle: jobTitle.trim() || null,
        companyName: companyName.trim() || null,
        jobDescription: jobDescription.trim() || null,
        companyDescription: companyDescription.trim() || null,
        linkedIn: linkedIn.trim() || null,
        theme,
      });
      setSession(res.accessToken); // adopt the fresh token so the new display name takes effect now
      if (uiLanguage) setLanguage(uiLanguage); // apply the UI language live (and persist it)
      qc.invalidateQueries({ queryKey: ["user-profile"] });
      setSaved(true);
    } catch (e) {
      setError(apiErrorMessage(e, t("preferencesSaveError")));
    } finally {
      setBusy(false);
    }
  }

  const field = "w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100";
  const labelSpan = "mb-1 block text-gray-600 dark:text-gray-300";

  return (
    <div className="space-y-3">
      <label className="block text-sm">
        <span className={labelSpan}>{t("displayName")}</span>
        <input
          name="diariz-display-name"
          autoComplete="off"
          value={fullName}
          onChange={(e) => setFullName(e.target.value)}
          placeholder={profile?.email ?? ""}
          className={field}
        />
      </label>

      <div className="grid grid-cols-2 gap-3">
        <label className="block text-sm">
          <span className={labelSpan}>{t("jobTitle")}</span>
          <input name="diariz-job-title" autoComplete="off" value={jobTitle} onChange={(e) => setJobTitle(e.target.value)} className={field} />
        </label>
        <label className="block text-sm">
          <span className={labelSpan}>{t("companyName")}</span>
          <input name="diariz-company" autoComplete="off" value={companyName} onChange={(e) => setCompanyName(e.target.value)} className={field} />
        </label>
      </div>

      <label className="block text-sm">
        <span className={labelSpan}>{t("linkedin")}</span>
        <input name="diariz-linkedin" autoComplete="off" value={linkedIn} onChange={(e) => setLinkedIn(e.target.value)} placeholder={t("linkedinPlaceholder")} className={field} />
      </label>

      <label className="block text-sm">
        <span className={labelSpan}>{t("jobDescription")}</span>
        <textarea value={jobDescription} onChange={(e) => setJobDescription(e.target.value)} rows={2} className={field} />
      </label>

      <label className="block text-sm">
        <span className={labelSpan}>{t("companyDescription")}</span>
        <textarea value={companyDescription} onChange={(e) => setCompanyDescription(e.target.value)} rows={2} className={field} />
      </label>

      <label className="block text-sm">
        <span className={labelSpan}>{t("nativeLanguage")}</span>
        <select value={nativeLanguage} onChange={(e) => setNativeLanguage(e.target.value)} aria-label={t("nativeLanguage")} className={field}>
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
        <span className={labelSpan}>{t("appLanguage")}</span>
        <select value={uiLanguage} onChange={(e) => setUiLanguage(e.target.value)} aria-label={t("appLanguage")} className={field}>
          <option value="">{t("followBrowser")}</option>
          {uiLanguages.map((l) => (
            <option key={l.code} value={l.code}>
              {l.englishName} ({l.nativeName})
            </option>
          ))}
        </select>
        <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">{t("appLanguageHint")}</span>
      </label>

      <label className="block text-sm">
        <span className={labelSpan}>{t("theme")}</span>
        <select value={theme} onChange={(e) => setTheme(e.target.value as ThemeChoice)} aria-label={t("theme")} className={field}>
          {THEMES.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {t(opt.key)}
            </option>
          ))}
        </select>
      </label>

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
