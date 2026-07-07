import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { api, apiErrorMessage } from "../lib/api";
import type { MinutesGenerationMode } from "../lib/types";
import { useAuth } from "../auth";
import { bytesToGb, gbToBytes } from "../lib/format";
import MaintenancePanel from "./MaintenancePanel";

type Tab = "ai" | "tools" | "quotas" | "maintenance" | "integration";

/// Settings modal. Every user gets Model Settings (per-user summarisation/model config) and Chat Tools
/// (the chat tool-calling switch + per-tool table); the Platform Administrator also gets Storage Quotas
/// and Maintenance. All tabs are saved together by a single OK/Cancel footer. The dialog is held at a
/// fixed height so it doesn't resize as the user flips between tabs.
export default function SettingsModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { isPlatformAdmin } = useAuth();
  const { data } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });
  const { data: platform } = useQuery({
    queryKey: ["platform-settings"],
    queryFn: api.getPlatformSettings,
    enabled: isPlatformAdmin,
  });

  const [tab, setTab] = useState<Tab>("ai");

  // AI settings.
  const [apiBase, setApiBase] = useState("");
  const [model, setModel] = useState("");
  const [apiKey, setApiKey] = useState<string | null>(null); // null = untouched, "" = clear, value = set
  // Chat tool calling: master switch + per-tool on/off map (keyed by tool name).
  const [toolsEnabled, setToolsEnabled] = useState(false);
  const [toolStates, setToolStates] = useState<Record<string, boolean>>({});
  // Reasoning: on/off + effort level ("low"|"medium"|"high").
  const [reasoningEnabled, setReasoningEnabled] = useState(false);
  const [reasoningEffort, setReasoningEffort] = useState("medium");

  // Storage quotas (GB inputs).
  const [starterGb, setStarterGb] = useState("");
  const [maxGb, setMaxGb] = useState("");
  // Platform-wide minutes generation mode; Platform-Admin only. String enum name on the wire.
  const [minutesMode, setMinutesMode] = useState<MinutesGenerationMode>("SingleCall");
  // Audio retention (Platform-Admin only): master switch, window in days, and server-local run time ("HH:mm").
  const [autoDeleteAudio, setAutoDeleteAudio] = useState(false);
  const [retentionDays, setRetentionDays] = useState("");
  const [deletionTime, setDeletionTime] = useState("03:00");
  // "Run now" (manual one-shot deletion pass) state.
  const [retentionRunBusy, setRetentionRunBusy] = useState(false);
  const [retentionRunMsg, setRetentionRunMsg] = useState<string | null>(null);
  // Integration (Platform-Admin only): master switch for user API access (personal tokens).
  const [apiAccessEnabled, setApiAccessEnabled] = useState(false);

  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (data) {
      setApiBase(data.apiBase ?? "");
      setModel(data.model ?? "");
      setToolsEnabled(data.toolsEnabled);
      setToolStates(Object.fromEntries(data.tools.map((tool) => [tool.name, tool.enabled])));
      setReasoningEnabled(data.reasoningEnabled);
      setReasoningEffort(data.reasoningEffort || "medium");
    }
  }, [data]);

  useEffect(() => {
    if (platform) {
      setStarterGb(String(bytesToGb(platform.starterQuotaBytes)));
      setMaxGb(String(bytesToGb(platform.maxQuotaBytes)));
      setMinutesMode(platform.minutesGenerationMode);
      setAutoDeleteAudio(platform.autoDeleteAudioEnabled);
      setRetentionDays(String(platform.audioRetentionDays));
      // "HH:mm:ss" on the wire -> "HH:mm" for the <input type="time">.
      setDeletionTime((platform.audioDeletionTimeOfDay ?? "03:00:00").slice(0, 5));
      setApiAccessEnabled(platform.apiAccessEnabled);
    }
  }, [platform]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function onOk() {
    setError(null);
    setBusy(true);
    try {
      await api.updateUserSettings({
        apiBase: apiBase.trim() || null,
        model: model.trim() || null,
        apiKey, // null leaves it unchanged; "" clears; a value sets it
        contextWindow: null, // no longer user-editable — always use the server default
        toolsEnabled,
        toolOverrides: toolStates,
        reasoningEnabled,
        reasoningEffort,
      });
      qc.invalidateQueries({ queryKey: ["user-settings"] });

      if (isPlatformAdmin) {
        const starter = gbToBytes(Number(starterGb));
        const max = gbToBytes(Number(maxGb));
        if (!(starter > 0) || !(max > 0)) throw new Error("Quota values must be greater than zero.");
        if (starter > max) throw new Error("The starter quota can't exceed the maximum quota.");
        const days = Number(retentionDays);
        if (!Number.isInteger(days) || days < 1) throw new Error(t("retentionDaysInvalid"));
        await api.updatePlatformSettings({
          starterQuotaBytes: starter,
          maxQuotaBytes: max,
          minutesGenerationMode: minutesMode,
          autoDeleteAudioEnabled: autoDeleteAudio,
          audioRetentionDays: days,
          // "HH:mm" from the input -> "HH:mm:ss" for the TimeOnly wire type.
          audioDeletionTimeOfDay: `${deletionTime || "03:00"}:00`,
          apiAccessEnabled,
        });
        qc.invalidateQueries({ queryKey: ["platform-settings"] });
      }
      onClose();
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  // Manual one-shot: run the audio-deletion pass immediately, using the saved retention window (independent
  // of the auto-delete toggle). Does not save or close the modal.
  async function runRetentionNow() {
    const days = platform?.audioRetentionDays ?? Number(retentionDays);
    if (!window.confirm(t("runAudioRetentionConfirm", { days }))) return;
    setRetentionRunMsg(null);
    setRetentionRunBusy(true);
    try {
      const { deleted } = await api.runAudioRetention();
      setRetentionRunMsg(t("runAudioRetentionResult", { count: deleted }));
      qc.invalidateQueries({ queryKey: ["recordings"] });
      qc.invalidateQueries({ queryKey: ["user-storage"] });
    } catch (e) {
      setRetentionRunMsg(apiErrorMessage(e));
    } finally {
      setRetentionRunBusy(false);
    }
  }

  // The backdrop does NOT close on click (OK/Cancel only) — prevents accidental dismissal. Escape still closes.
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-label="Settings"
        className="flex h-[85vh] w-full max-w-3xl flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="border-b px-5 pt-4 dark:border-gray-700">
          <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{t("settingsTitle")}</h2>
          <div className="flex gap-1" role="tablist" aria-label={t("settingsTitle")}>
            <TabButton active={tab === "ai"} onClick={() => setTab("ai")}>
              {t("aiSettings")}
            </TabButton>
            <TabButton active={tab === "tools"} onClick={() => setTab("tools")}>
              {t("chatToolsTab")}
            </TabButton>
            {isPlatformAdmin && (
              <>
                <TabButton active={tab === "quotas"} onClick={() => setTab("quotas")}>
                  {t("storageQuotas")}
                </TabButton>
                <TabButton active={tab === "maintenance"} onClick={() => setTab("maintenance")}>
                  {t("maintenanceTab")}
                </TabButton>
                <TabButton active={tab === "integration"} onClick={() => setTab("integration")}>
                  {t("integrationTab")}
                </TabButton>
              </>
            )}
          </div>
        </div>

        {/* Non-login field names + per-field autoComplete="off" stop password managers autofilling these. */}
        <div className="min-h-0 flex-1 overflow-y-auto p-5">
          {tab === "ai" ? (
            <div className="space-y-3">
              <p className="text-xs text-gray-500 dark:text-gray-400">{t("aiIntro")}</p>
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("summaryEndpoint")}</span>
                <input
                  name="diariz-summary-endpoint"
                  autoComplete="off"
                  value={apiBase}
                  onChange={(e) => setApiBase(e.target.value)}
                  placeholder={data?.defaultApiBase ? t("defaultValue", { value: data.defaultApiBase }) : "https://api.openai.com/v1"}
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
              </label>
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("summaryModel")}</span>
                <input
                  name="diariz-summary-model"
                  autoComplete="off"
                  value={model}
                  onChange={(e) => setModel(e.target.value)}
                  placeholder={data?.defaultModel ? t("defaultValue", { value: data.defaultModel }) : "gpt-4o-mini"}
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
              </label>
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">
                  {t("summaryApiKey")}
                  {data?.hasApiKey && apiKey === null && (
                    <span className="ml-1 text-green-600 dark:text-green-400">{t("keySet")}</span>
                  )}
                </span>
                <input
                  type="password"
                  name="diariz-summary-key"
                  autoComplete="new-password"
                  value={apiKey ?? ""}
                  onChange={(e) => setApiKey(e.target.value)}
                  placeholder={
                    data?.hasApiKey
                      ? t("keyKeepBlank")
                      : data?.serverHasApiKey
                        ? t("keyServerDefault")
                        : "sk-…"
                  }
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
                {data?.hasApiKey && (
                  <button
                    type="button"
                    onClick={() => setApiKey("")}
                    className="mt-1 text-xs text-red-600 hover:underline dark:text-red-400"
                  >
                    {t("clearStoredKey")}
                  </button>
                )}
              </label>
              {apiKey === "" && (
                <p className="text-xs text-amber-600 dark:text-amber-400">{t("keyWillClear")}</p>
              )}
              {/* Reasoning: on/off + effort level (only for reasoning-capable models). Kept above the tools list. */}
              <div className="border-t pt-3 dark:border-gray-700">
                <label className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={reasoningEnabled}
                    onChange={(e) => setReasoningEnabled(e.target.checked)}
                  />
                  <span className="font-medium text-gray-700 dark:text-gray-200">{t("reasoningEnabled")}</span>
                </label>
                <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">{t("reasoningHint")}</p>
                {reasoningEnabled && (
                  <label className="mt-2 block text-sm">
                    <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("reasoningLevel")}</span>
                    <select
                      value={reasoningEffort}
                      onChange={(e) => setReasoningEffort(e.target.value)}
                      aria-label={t("reasoningLevel")}
                      className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                    >
                      <option value="low">{t("reasoningLow")}</option>
                      <option value="medium">{t("reasoningMedium")}</option>
                      <option value="high">{t("reasoningHigh")}</option>
                    </select>
                  </label>
                )}
              </div>

              {/* Minutes generation mode - platform-wide, Platform-Admin only. Applies from the next run. */}
              {isPlatformAdmin && (
                <div className="border-t pt-3 dark:border-gray-700">
                  <label className="block text-sm">
                    <span className="mb-1 block font-medium text-gray-700 dark:text-gray-200">{t("minutesModeLabel")}</span>
                    <select
                      value={minutesMode}
                      onChange={(e) => setMinutesMode(e.target.value as MinutesGenerationMode)}
                      aria-label={t("minutesModeLabel")}
                      className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                    >
                      <option value="SingleCall">{t("minutesModeSingle")}</option>
                      <option value="PerSection">{t("minutesModePerSection")}</option>
                    </select>
                  </label>
                  <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">{t("minutesModeHint")}</p>
                </div>
              )}
            </div>
          ) : tab === "tools" ? (
            /* Chat tools: a master switch plus a per-tool table (checkboxes disabled when the master is off). */
            <div className="space-y-3">
              <label className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={toolsEnabled}
                  onChange={(e) => setToolsEnabled(e.target.checked)}
                />
                <span className="font-medium text-gray-700 dark:text-gray-200">{t("chatToolsEnabled")}</span>
              </label>
              <p className="text-xs text-gray-400 dark:text-gray-500">{t("chatToolsHint")}</p>
              {data && data.tools.length > 0 && (
                <div className="overflow-x-auto rounded border dark:border-gray-700">
                  <table className="w-full text-left text-sm">
                    <thead className="bg-gray-50 text-xs text-gray-500 dark:bg-gray-800 dark:text-gray-400">
                      <tr>
                        <th scope="col" className="w-12 px-2 py-1.5 text-center font-medium">{t("toolColEnabled")}</th>
                        <th scope="col" className="px-2 py-1.5 font-medium">{t("toolColTool")}</th>
                        <th scope="col" className="px-2 py-1.5 font-medium">{t("toolColDescription")}</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y dark:divide-gray-700">
                      {data.tools.map((tool) => (
                        <tr key={tool.name} className={toolsEnabled ? "" : "opacity-50"}>
                          <td className="px-2 py-1.5 text-center align-top">
                            <input
                              type="checkbox"
                              aria-label={tool.title}
                              disabled={!toolsEnabled}
                              checked={toolStates[tool.name] ?? tool.enabled}
                              onChange={(e) => setToolStates((s) => ({ ...s, [tool.name]: e.target.checked }))}
                            />
                          </td>
                          <td className="px-2 py-1.5 align-top text-gray-700 dark:text-gray-200">{tool.title}</td>
                          <td className="px-2 py-1.5 align-top text-xs text-gray-500 dark:text-gray-400">{tool.description}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          ) : tab === "quotas" ? (
            <div className="space-y-3">
              <p className="text-xs text-gray-500 dark:text-gray-400">{t("quotasIntro")}</p>
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("starterQuota")}</span>
                <input
                  type="number"
                  min={1}
                  step={0.5}
                  value={starterGb}
                  onChange={(e) => setStarterGb(e.target.value)}
                  aria-label={t("starterQuota")}
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
              </label>
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("maxQuota")}</span>
                <input
                  type="number"
                  min={1}
                  step={0.5}
                  value={maxGb}
                  onChange={(e) => setMaxGb(e.target.value)}
                  aria-label={t("maxQuota")}
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
              </label>

              {/* Audio retention: opt-in nightly deletion of old recordings' audio (transcripts are kept). */}
              <div className="border-t pt-3 dark:border-gray-700">
                <label className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={autoDeleteAudio}
                    onChange={(e) => setAutoDeleteAudio(e.target.checked)}
                  />
                  <span className="font-medium text-gray-700 dark:text-gray-200">{t("autoDeleteAudio")}</span>
                </label>
                <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">{t("autoDeleteAudioHint")}</p>
                <div className="mt-2 flex gap-3">
                  <label className="block flex-1 text-sm">
                    <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("retentionDays")}</span>
                    <input
                      type="number"
                      min={1}
                      step={1}
                      value={retentionDays}
                      onChange={(e) => setRetentionDays(e.target.value)}
                      disabled={!autoDeleteAudio}
                      aria-label={t("retentionDays")}
                      className="w-full rounded border px-2 py-1 text-sm disabled:opacity-50 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                    />
                  </label>
                  <label className="block flex-1 text-sm">
                    <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("deletionTime")}</span>
                    <input
                      type="time"
                      value={deletionTime}
                      onChange={(e) => setDeletionTime(e.target.value)}
                      disabled={!autoDeleteAudio}
                      aria-label={t("deletionTime")}
                      className="w-full rounded border px-2 py-1 text-sm disabled:opacity-50 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                    />
                  </label>
                </div>
                {/* Manual trigger: runs the same deletion pass now (uses the saved window, regardless of the switch). */}
                <div className="mt-3 flex items-center gap-3">
                  <button
                    type="button"
                    onClick={runRetentionNow}
                    disabled={retentionRunBusy}
                    className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
                  >
                    {retentionRunBusy ? t("runAudioRetentionRunning") : t("runAudioRetentionNow")}
                  </button>
                  {retentionRunMsg && (
                    <span className="text-xs text-gray-600 dark:text-gray-300">{retentionRunMsg}</span>
                  )}
                </div>
              </div>
            </div>
          ) : tab === "maintenance" ? (
            <MaintenancePanel />
          ) : (
            /* Integration: platform-wide toggles for external access. API access is off by default. */
            <div className="space-y-3">
              <label className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={apiAccessEnabled}
                  onChange={(e) => setApiAccessEnabled(e.target.checked)}
                />
                <span className="font-medium text-gray-700 dark:text-gray-200">{t("apiAccessEnabledLabel")}</span>
              </label>
              <p className="text-xs text-gray-400 dark:text-gray-500">{t("apiAccessEnabledHelp")}</p>
              <Link
                to="/developers/api"
                onClick={onClose}
                className="inline-block text-xs text-indigo-600 hover:underline dark:text-indigo-400"
              >
                {t("apiViewReference")} →
              </Link>
            </div>
          )}
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

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={`-mb-px rounded-t border-b-2 px-3 py-1.5 text-sm ${
        active
          ? "border-gray-900 font-medium text-gray-900 dark:border-gray-100 dark:text-gray-100"
          : "border-transparent text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200"
      }`}
    >
      {children}
    </button>
  );
}
