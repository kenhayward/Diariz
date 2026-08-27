import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import SettingRow, { SETTING_CONTROL, SETTING_GRID } from "./SettingRow";

/// How confident Diariz has to be before it names a voice, plus the re-scan that applies the current
/// settings to recordings already transcribed.
///
/// **Controlled, not self-saving.** The platform settings endpoint takes the whole object, so a panel that
/// saved on its own would overwrite whatever the rest of the form had unsaved. The four values live in the
/// parent's form state and are written by its Save.
///
/// The re-scan is the exception: it is its own endpoint and changes nothing about the settings, so it acts
/// immediately - and answers with a preview first.
export default function IdentificationSettings({
  threshold,
  setThreshold,
  band,
  setBand,
  margin,
  setMargin,
  minSpeechMs,
  setMinSpeechMs,
}: {
  threshold: string;
  setThreshold: (v: string) => void;
  band: string;
  setBand: (v: string) => void;
  margin: string;
  setMargin: (v: string) => void;
  minSpeechMs: string;
  setMinSpeechMs: (v: string) => void;
}) {
  const { t } = useTranslation("admin");
  const [busy, setBusy] = useState(false);
  const [preview, setPreview] = useState<{ applied: number; suggested: number } | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function run(dryRun: boolean) {
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const r = await api.rescanIdentification(dryRun);
      if (dryRun) {
        setPreview({ applied: r.applied, suggested: r.suggested });
      } else {
        setPreview(null);
        setMessage(t("rescanDone", { applied: r.applied, suggested: r.suggested }));
      }
    } catch (e) {
      setError(apiErrorMessage(e, t("rescanFailed")));
    } finally {
      setBusy(false);
    }
  }

  const field = (
    label: string,
    hint: string,
    value: string,
    onChange: (v: string) => void,
    step: string,
    min: number,
  ) => (
    <SettingRow key={label} label={label} hint={hint}>
      {(id) => (
        <input
          id={id}
          type="number"
          min={min}
          step={step}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          className={SETTING_CONTROL}
        />
      )}
    </SettingRow>
  );

  return (
    <section className="flex flex-col gap-2">
      <h3 className="text-sm font-semibold text-gray-800 dark:text-gray-100">{t("identificationTitle")}</h3>
      <p className="text-xs text-gray-500 dark:text-gray-400">{t("identificationIntro")}</p>

      <div className={SETTING_GRID} data-setting-grid>
        {field(t("identThresholdLabel"), t("identThresholdHint"), threshold, setThreshold, "0.01", 0)}
        {field(t("identBandLabel"), t("identBandHint"), band, setBand, "0.01", 0)}
        {field(t("identMarginLabel"), t("identMarginHint"), margin, setMargin, "0.01", 0)}
        {field(t("identMinSpeechLabel"), t("identMinSpeechHint"), minSpeechMs, setMinSpeechMs, "500", 0)}
      </div>

      <div className="rounded border p-3 dark:border-gray-700">
        <p className="text-xs text-gray-500 dark:text-gray-400">{t("rescanIntro")}</p>
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => void run(true)}
            disabled={busy}
            className="rounded border px-2 py-1 text-xs disabled:opacity-50 dark:border-gray-600 dark:text-gray-200"
          >
            {t("rescanPreview")}
          </button>
          {/* Only offered once a preview has said what it would do. Applying names across a whole library
              without being told the number first is not a decision anyone can make. */}
          {preview && (
            <button
              type="button"
              onClick={() => void run(false)}
              disabled={busy}
              className="rounded border border-blue-400 px-2 py-1 text-xs text-blue-700 disabled:opacity-50 dark:border-blue-600 dark:text-blue-300"
            >
              {t("rescanApply")}
            </button>
          )}
        </div>
        {preview && (
          <p className="mt-2 text-xs text-gray-700 dark:text-gray-200">
            {t("rescanPreviewResult", { applied: preview.applied, suggested: preview.suggested })}
          </p>
        )}
        {message && <p className="mt-2 text-xs text-green-700 dark:text-green-400">{message}</p>}
        {error && <p className="mt-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
      </div>
    </section>
  );
}
