import { lazy, Suspense, useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { MinutesGenerationMode, WebhookCreated, WorkflowSignal } from "../lib/types";
import { useAuth } from "../auth";
import { bytesToGb, gbToBytes } from "../lib/format";
import { platformWebhookEvents, SIGNAL_EXEMPT_EVENT_KEYS } from "../lib/webhookEvents";
import PanelModal from "./PanelModal";
import MaintenancePanel from "./MaintenancePanel";
import IdentificationSettings from "./settings/IdentificationSettings";

// Lazily loaded: both are large, and most visits to Settings never open either. They are the same
// components the /admin/* routes render - `embedded` only drops the page chrome this modal provides.
const LlmUsage = lazy(() => import("../pages/LlmUsage"));
const LlmModels = lazy(() => import("../pages/LlmModels"));
const ApiReference = lazy(() => import("../pages/ApiReference"));
import FeedbackPanel from "./FeedbackPanel";

type Tab = "ai" | "quotas" | "maintenance" | "feedback" | "integration";

/// Platform settings modal - Platform Administrator only (the account menu hides it otherwise). Holds the
/// platform-wide AI generation policy (minutes mode + the global LLM timeout), storage quotas + audio
/// retention, maintenance (backup/restore, backfills), and the integration toggles. Personal preferences
/// (Model Settings, Chat Tools, Recordings) live in the Preferences modal. A single OK/Cancel footer saves
/// every tab together; the dialog is held at a fixed height so it doesn't resize as you flip between tabs.
export default function SettingsModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { isPlatformAdmin } = useAuth();
  const { data: platform } = useQuery({
    queryKey: ["platform-settings"],
    queryFn: api.getPlatformSettings,
    enabled: isPlatformAdmin,
  });

  const [tab, setTab] = useState<Tab>("ai");
  /// Which admin panel is open over this modal, and - for the usage log - the filter it was asked to open
  /// on. Opened in place rather than navigated to: the old `target="_blank"` links left the installed PWA
  /// and the desktop shell for the system browser, where the admin is not signed in.
  const [panel, setPanel] = useState<{ which: "usage" | "models" | "api"; query?: string } | null>(null);

  // Storage quotas (GB inputs).
  const [starterGb, setStarterGb] = useState("");
  const [maxGb, setMaxGb] = useState("");
  // Platform-wide minutes generation mode. String enum name on the wire.
  const [minutesMode, setMinutesMode] = useState<MinutesGenerationMode>("SingleCall");
  // Platform-wide LLM request timeout in seconds. Default 120.
  const [llmTimeout, setLlmTimeout] = useState("120");
  // LLM usage log: master switch, retention window in days (0 = keep forever), and whether streaming
  // requests ask for token counts. All default on / 90 days.
  const [llmUsageLoggingEnabled, setLlmUsageLoggingEnabled] = useState(true);
  const [llmUsageRetentionDays, setLlmUsageRetentionDays] = useState("90");
  const [llmStreamUsageEnabled, setLlmStreamUsageEnabled] = useState(true);
  // Voice identification: how close a match must be to be applied, to be asked about, how far it must beat
  // the runner-up, and how little speech is too little to judge. Held as strings like the other numeric
  // fields so a half-typed value does not become NaN mid-edit.
  const [identThreshold, setIdentThreshold] = useState("0.3");
  const [identBand, setIdentBand] = useState("0.4");
  const [identMargin, setIdentMargin] = useState("0.05");
  const [identMinSpeechMs, setIdentMinSpeechMs] = useState("3000");
  // Audio retention: master switch, window in days, and server-local run time ("HH:mm").
  const [autoDeleteAudio, setAutoDeleteAudio] = useState(false);
  const [retentionDays, setRetentionDays] = useState("");
  const [deletionTime, setDeletionTime] = useState("03:00");
  // "Run now" (manual one-shot deletion pass) state.
  const [retentionRunBusy, setRetentionRunBusy] = useState(false);
  const [retentionRunMsg, setRetentionRunMsg] = useState<string | null>(null);
  // Integration: master switch for user API access (personal tokens).
  const [apiAccessEnabled, setApiAccessEnabled] = useState(false);
  // Integration: master switch for Claude / MCP access (personal MCP tokens). On by default.
  const [mcpAccessEnabled, setMcpAccessEnabled] = useState(true);
  // Integration: master switch for outbound webhooks (meeting-event automations). Off by default.
  const [webhooksEnabled, setWebhooksEnabled] = useState(false);

  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (platform) {
      setStarterGb(String(bytesToGb(platform.starterQuotaBytes)));
      setMaxGb(String(bytesToGb(platform.maxQuotaBytes)));
      setMinutesMode(platform.minutesGenerationMode);
      setLlmTimeout(String(platform.llmTimeoutSeconds ?? 120));
      setIdentThreshold(String(platform.identificationThreshold ?? 0.3));
      setIdentBand(String(platform.identificationConfirmBand ?? 0.4));
      setIdentMargin(String(platform.identificationMargin ?? 0.05));
      setIdentMinSpeechMs(String(platform.identificationMinSpeechMs ?? 3000));
      setLlmUsageLoggingEnabled(platform.llmUsageLoggingEnabled ?? true);
      setLlmUsageRetentionDays(String(platform.llmUsageRetentionDays ?? 90));
      setLlmStreamUsageEnabled(platform.llmStreamUsageEnabled ?? true);
      setAutoDeleteAudio(platform.autoDeleteAudioEnabled);
      setRetentionDays(String(platform.audioRetentionDays));
      // "HH:mm:ss" on the wire -> "HH:mm" for the <input type="time">.
      setDeletionTime((platform.audioDeletionTimeOfDay ?? "03:00:00").slice(0, 5));
      setApiAccessEnabled(platform.apiAccessEnabled);
      setMcpAccessEnabled(platform.mcpAccessEnabled);
      setWebhooksEnabled(platform.webhooksEnabled);
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
      const starter = gbToBytes(Number(starterGb));
      const max = gbToBytes(Number(maxGb));
      if (!(starter > 0) || !(max > 0)) throw new Error("Quota values must be greater than zero.");
      if (starter > max) throw new Error("The starter quota can't exceed the maximum quota.");
      const days = Number(retentionDays);
      if (!Number.isInteger(days) || days < 1) throw new Error(t("retentionDaysInvalid"));
      const timeout = Number(llmTimeout);
      if (!Number.isInteger(timeout) || timeout < 5) throw new Error(t("llmTimeoutInvalid"));
      const usageRetentionDays = Number(llmUsageRetentionDays);
      if (!Number.isInteger(usageRetentionDays) || usageRetentionDays < 0) throw new Error(t("llmUsageRetentionInvalid"));
      const identThresholdNum = Number(identThreshold);
      const identBandNum = Number(identBand);
      if (!(identThresholdNum > 0) || identThresholdNum > 2) throw new Error(t("identThresholdInvalid"));
      // Inverted, nothing would ever be named automatically and every match would arrive as a question -
      // a setting that looks perfectly reasonable in isolation.
      if (identBandNum < identThresholdNum) throw new Error(t("identBandInvalid"));
      await api.updatePlatformSettings({
        starterQuotaBytes: starter,
        maxQuotaBytes: max,
        minutesGenerationMode: minutesMode,
        autoDeleteAudioEnabled: autoDeleteAudio,
        audioRetentionDays: days,
        // "HH:mm" from the input -> "HH:mm:ss" for the TimeOnly wire type.
        audioDeletionTimeOfDay: `${deletionTime || "03:00"}:00`,
        apiAccessEnabled,
        mcpAccessEnabled,
        webhooksEnabled,
        llmTimeoutSeconds: timeout,
        llmUsageLoggingEnabled,
        llmUsageRetentionDays: usageRetentionDays,
        llmStreamUsageEnabled,
        identificationThreshold: identThresholdNum,
        identificationConfirmBand: identBandNum,
        identificationMargin: Number(identMargin),
        identificationMinSpeechMs: Number(identMinSpeechMs),
      });
      qc.invalidateQueries({ queryKey: ["platform-settings"] });
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
              {t("platformAiTab")}
            </TabButton>
            <TabButton active={tab === "quotas"} onClick={() => setTab("quotas")}>
              {t("storageQuotas")}
            </TabButton>
            <TabButton active={tab === "maintenance"} onClick={() => setTab("maintenance")}>
              {t("maintenanceTab")}
            </TabButton>
            <TabButton active={tab === "feedback"} onClick={() => setTab("feedback")}>
              {t("feedbackTab")}
            </TabButton>
            <TabButton active={tab === "integration"} onClick={() => setTab("integration")}>
              {t("integrationTab")}
            </TabButton>
          </div>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto p-5">
          {tab === "ai" ? (
            /* Platform-wide AI generation policy: minutes mode + the global LLM request timeout. */
            <div className="space-y-3">
              <p className="text-xs text-gray-500 dark:text-gray-400">{t("platformAiIntro")}</p>
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
                <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">{t("minutesModeHint")}</span>
              </label>
              <label className="block text-sm">
                <span className="mb-1 block font-medium text-gray-700 dark:text-gray-200">{t("llmTimeoutLabel")}</span>
                <input
                  type="number"
                  min={5}
                  step={1}
                  value={llmTimeout}
                  onChange={(e) => setLlmTimeout(e.target.value)}
                  aria-label={t("llmTimeoutLabel")}
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
                <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">{t("llmTimeoutHint")}</span>
              </label>

              {/* Its own component rather than four more fields inline: this modal was already 845 lines,
                  and the panel carries a re-scan control with state of its own. Controlled, because the
                  settings endpoint takes the whole object and a self-saving panel would clobber the rest. */}
              <IdentificationSettings
                threshold={identThreshold}
                setThreshold={setIdentThreshold}
                band={identBand}
                setBand={setIdentBand}
                margin={identMargin}
                setMargin={setIdentMargin}
                minSpeechMs={identMinSpeechMs}
                setMinSpeechMs={setIdentMinSpeechMs}
              />

              {/* LLM usage log: master switch, retention window (0 = keep forever), and whether streaming
                  calls ask for token counts (consumed since 0.217.0 to report real tokens/duration on
                  streamed calls). */}
              <div className="border-t pt-3 dark:border-gray-700">
                <label className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={llmUsageLoggingEnabled}
                    onChange={(e) => setLlmUsageLoggingEnabled(e.target.checked)}
                  />
                  <span className="font-medium text-gray-700 dark:text-gray-200">{t("llmUsageLoggingLabel")}</span>
                </label>
                <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">{t("llmUsageLoggingHint")}</p>

                <label className="mt-2 block text-sm">
                  <span className="mb-1 block font-medium text-gray-700 dark:text-gray-200">{t("llmUsageRetentionLabel")}</span>
                  <input
                    type="number"
                    min={0}
                    step={1}
                    value={llmUsageRetentionDays}
                    onChange={(e) => setLlmUsageRetentionDays(e.target.value)}
                    aria-label={t("llmUsageRetentionLabel")}
                    className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                  />
                  <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">{t("llmUsageRetentionHint")}</span>
                </label>

                <label className="mt-2 flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={llmStreamUsageEnabled}
                    onChange={(e) => setLlmStreamUsageEnabled(e.target.checked)}
                  />
                  <span className="font-medium text-gray-700 dark:text-gray-200">{t("llmStreamUsageLabel")}</span>
                </label>
                <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">{t("llmStreamUsageHint")}</p>

                <button
                  type="button"
                  onClick={() => setPanel({ which: "usage" })}
                  className="mt-2 inline-block text-xs text-indigo-600 hover:underline dark:text-indigo-400"
                >
                  {t("llmUsageViewLog")} →
                </button>

                <button
                  type="button"
                  onClick={() => setPanel({ which: "models" })}
                  className="mt-2 ml-4 inline-block text-xs text-indigo-600 hover:underline dark:text-indigo-400"
                >
                  {t("llmModelsManage")} →
                </button>
              </div>
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
          ) : tab === "feedback" ? (
            <FeedbackPanel />
          ) : (
            /* Integration: platform-wide toggles for external access. API access is off by default. */
            <div className="space-y-3">
              {/* One row per toggle: checkbox + label, with the hint inline to the right (wraps when narrow). */}
              <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-sm">
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={apiAccessEnabled}
                    onChange={(e) => setApiAccessEnabled(e.target.checked)}
                  />
                  <span className="font-medium text-gray-700 dark:text-gray-200">{t("apiAccessEnabledLabel")}</span>
                </label>
                <span className="text-xs text-gray-400 dark:text-gray-500">{t("apiAccessEnabledHelp")}</span>
              </div>

              <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-sm">
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={mcpAccessEnabled}
                    onChange={(e) => setMcpAccessEnabled(e.target.checked)}
                  />
                  <span className="font-medium text-gray-700 dark:text-gray-200">{t("mcpAccessEnabledLabel")}</span>
                </label>
                <span className="text-xs text-gray-400 dark:text-gray-500">{t("mcpAccessEnabledHelp")}</span>
              </div>

              <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-sm">
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={webhooksEnabled}
                    onChange={(e) => setWebhooksEnabled(e.target.checked)}
                  />
                  <span className="font-medium text-gray-700 dark:text-gray-200">{t("webhooksEnabledLabel")}</span>
                </label>
                <span className="text-xs text-gray-400 dark:text-gray-500">{t("webhooksEnabledHelp")}</span>
              </div>
              <button
                type="button"
                onClick={() => setPanel({ which: "api" })}
                className="inline-block text-xs text-indigo-600 hover:underline dark:text-indigo-400"
              >
                {t("apiViewReference")} →
              </button>

              {webhooksEnabled && <WorkflowSignalsSection />}
              {webhooksEnabled && <PlatformAutomationsSection />}
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

      {panel && (
        <PanelModal
          title={
            panel.which === "usage"
              ? t("llmUsageViewerTitle")
              : panel.which === "api"
                ? t("apiReferenceTitle")
                : t("llmModelsTitle")
          }
          onClose={() => setPanel(null)}
        >
          <Suspense fallback={null}>
            {panel.which === "api" ? (
              <ApiReference embedded />
            ) : panel.which === "usage" ? (
              <LlmUsage embedded initialQuery={panel.query ?? ""} />
            ) : (
              // Switches panels rather than navigating: a route change here would unmount the settings
              // modal and, in the desktop shell, leave the app.
              <LlmModels embedded onOpenUsageLog={(query) => setPanel({ which: "usage", query })} />
            )}
          </Suspense>
        </PanelModal>
      )}
    </div>
  );
}

/// Admin management of workflow signals: named conditions a formula can attach, driving webhook
/// `signalFilter` matching. Lists every signal (active + inactive), lets the admin add one (key + label +
/// optional description), toggle it active, or delete it. Only rendered when webhooks are enabled - signals
/// are meaningless without the webhook system that consumes them.
function WorkflowSignalsSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: signals = [] } = useQuery({ queryKey: ["workflow-signals-all"], queryFn: api.listAllWorkflowSignals });

  const [key, setKey] = useState("");
  const [label, setLabel] = useState("");
  const [description, setDescription] = useState("");
  const [error, setError] = useState<string | null>(null);

  const invalidate = () => qc.invalidateQueries({ queryKey: ["workflow-signals-all"] });

  const create = useMutation({
    mutationFn: () => api.createWorkflowSignal({ key: key.trim(), label: label.trim(), description: description.trim() || null }),
    onSuccess: () => {
      setKey("");
      setLabel("");
      setDescription("");
      setError(null);
      void invalidate();
    },
    onError: (e) => setError(apiErrorMessage(e)),
  });

  const toggleActive = useMutation({
    mutationFn: (signal: WorkflowSignal) =>
      api.updateWorkflowSignal(signal.id, { label: signal.label, description: signal.description, isActive: !signal.isActive }),
    onSuccess: invalidate,
    onError: (e) => setError(apiErrorMessage(e)),
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.deleteWorkflowSignal(id),
    onSuccess: invalidate,
    onError: (e) => setError(apiErrorMessage(e)),
  });

  function onDelete(signal: WorkflowSignal) {
    setError(null);
    if (window.confirm(t("signalDelete") + `: ${signal.label}?`)) remove.mutate(signal.id);
  }

  return (
    <div className="border-t pt-3 dark:border-gray-700">
      <h3 className="mb-1 text-sm font-medium text-gray-700 dark:text-gray-200">{t("signalsHeading")}</h3>
      <p className="mb-2 text-xs text-gray-500 dark:text-gray-400">{t("signalsIntro")}</p>
      {error && <p className="mb-2 text-xs text-red-600 dark:text-red-400">{error}</p>}

      {signals.length > 0 && (
        <table className="mb-2 w-full text-sm">
          <tbody>
            {signals.map((s) => (
              <tr key={s.id} className="border-t align-middle dark:border-gray-700 dark:text-gray-200">
                <td className="py-1 pr-2 font-mono text-xs">{s.key}</td>
                <td className="py-1 pr-2">{s.label}</td>
                <td className="px-2 text-center">
                  <label className="inline-flex items-center gap-1 text-xs">
                    <input
                      type="checkbox"
                      checked={s.isActive}
                      aria-label={`${s.label}: ${t("signalActive")}`}
                      onChange={() => toggleActive.mutate(s)}
                    />
                    {t("signalActive")}
                  </label>
                </td>
                <td className="py-1 text-right">
                  <button
                    type="button"
                    onClick={() => onDelete(s)}
                    className="rounded px-2 py-1 text-xs text-red-600 hover:bg-red-50 dark:hover:bg-red-900/30"
                  >
                    {t("signalDelete")}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <form
        className="space-y-1"
        onSubmit={(e) => {
          e.preventDefault();
          setError(null);
          if (key.trim() && label.trim()) create.mutate();
        }}
      >
        <div className="flex flex-wrap items-end gap-2">
          <label className="text-xs">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("signalKey")}</span>
            <input
              value={key}
              onChange={(e) => setKey(e.target.value)}
              aria-label={t("signalKey")}
              className="w-40 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>
          <label className="text-xs">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("signalLabel")}</span>
            <input
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              aria-label={t("signalLabel")}
              className="w-40 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>
          <label className="min-w-[10rem] flex-1 text-xs">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("signalDescription")}</span>
            <input
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              aria-label={t("signalDescription")}
              className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>
          <button
            type="submit"
            disabled={!key.trim() || !label.trim()}
            className="shrink-0 rounded bg-blue-600 px-3 py-1 text-sm text-white disabled:opacity-50"
          >
            {t("signalAdd")}
          </button>
        </div>
        <p className="text-xs text-gray-400 dark:text-gray-500">{t("signalKeyHint")}</p>
      </form>
    </div>
  );
}

/// Admin management of Platform (admin-owned, signal-routed) webhook subscriptions - unlike a user's own
/// Automations, these route by Workflow Signal across every user rather than belonging to one person. Reuses
/// the personal Automations create-form shape (name + destination URL + event checkboxes) plus a Workflow
/// Signal multi-select sourced from the signals section's own query; the server rejects an empty signal
/// filter (a platform automation with no signal never fires), so the create is blocked client-side to match -
/// EXCEPT when every chosen event is signal-exempt (Feedback Received carries no signal and fires whatever
/// the filter says), which the server allows and this form must not block. The platform picker also offers
/// the platform-only event types, which no personal automation may have.
/// The signing secret is shown exactly once, right after creation, using the same amber show-once pattern as
/// the personal Automations section. Only rendered when webhooks are enabled.
function PlatformAutomationsSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: subs = [] } = useQuery({ queryKey: ["platform-webhooks"], queryFn: api.listPlatformWebhooks });
  const { data: signals = [] } = useQuery({ queryKey: ["workflow-signals-all"], queryFn: api.listAllWorkflowSignals });

  const EVENTS = platformWebhookEvents(t);

  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [selectedEvents, setSelectedEvents] = useState<Record<string, boolean>>({});
  const [selectedSignals, setSelectedSignals] = useState<Record<string, boolean>>({});
  const [includeFeedbackText, setIncludeFeedbackText] = useState(false);
  const [created, setCreated] = useState<WebhookCreated | null>(null);
  const [error, setError] = useState<string | null>(null);

  const eventTypes = EVENTS.map((e) => e.key).filter((key) => selectedEvents[key]);
  const signalFilter = signals.filter((s) => selectedSignals[s.id]).map((s) => s.key);
  const signalLabel = (key: string) => signals.find((s) => s.key === key)?.label ?? key;
  const eventLabel = (key: string) => EVENTS.find((e) => e.key === key)?.label ?? key;
  // The feedback-text opt-in only means anything to a feedback delivery, so it is only offered once that
  // event is chosen - and its value is only sent then, so it cannot be left set from an earlier edit.
  const wantsFeedback = eventTypes.includes("feedback.submitted");
  const needsSignal = eventTypes.some((key) => !SIGNAL_EXEMPT_EVENT_KEYS.includes(key));

  const invalidate = () => qc.invalidateQueries({ queryKey: ["platform-webhooks"] });

  const copy = (text: string) => void navigator.clipboard?.writeText(text);

  const create = useMutation({
    mutationFn: () =>
      api.createPlatformWebhook({
        name: name.trim() || t("automationDefaultName"),
        url,
        eventTypes,
        signalFilter,
        includeFeedbackText: wantsFeedback ? includeFeedbackText : false,
      }),
    onSuccess: (result) => {
      setCreated(result);
      setName("");
      setUrl("");
      setSelectedEvents({});
      setSelectedSignals({});
      setIncludeFeedbackText(false);
      setError(null);
      void invalidate();
    },
    onError: (e) => setError(apiErrorMessage(e, t("automationCreateError"))),
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.deletePlatformWebhook(id),
    onSuccess: invalidate,
    onError: (e) => setError(apiErrorMessage(e, t("automationCreateError"))),
  });

  function onCreate() {
    setError(null);
    if (!url.trim() || eventTypes.length === 0) return;
    if (needsSignal && signalFilter.length === 0) {
      setError(t("platformAutomationNeedsSignal"));
      return;
    }
    create.mutate();
  }

  function onDelete(id: string) {
    setError(null);
    if (window.confirm(t("automationDelete") + "?")) remove.mutate(id);
  }

  const btn =
    "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="border-t pt-3 dark:border-gray-700">
      <h3 className="mb-1 text-sm font-medium text-gray-700 dark:text-gray-200">{t("platformAutomationsHeading")}</h3>
      <p className="mb-2 text-xs text-gray-400 dark:text-gray-500">{t("platformAutomationsHint")}</p>
      {error && <p className="mb-2 text-xs text-red-600 dark:text-red-400">{error}</p>}

      {subs.length > 0 && (
        <div className="mb-3 space-y-2">
          {subs.map((s) => (
            <div key={s.id} className="rounded border p-2 dark:border-gray-700">
              <div className="flex items-center justify-between gap-2">
                <span className="truncate text-sm font-medium text-gray-800 dark:text-gray-100">{s.name}</span>
                <button
                  type="button"
                  onClick={() => onDelete(s.id)}
                  className="shrink-0 rounded px-2 py-1 text-xs text-red-600 hover:bg-red-50 dark:hover:bg-red-900/30"
                >
                  {t("automationDelete")}
                </button>
              </div>
              <p className="mt-0.5 truncate text-xs text-gray-400 dark:text-gray-500">{s.url}</p>
              <div className="mt-1 flex flex-wrap gap-1">
                {s.eventTypes.map((key) => (
                  <span
                    key={key}
                    className="rounded-full border px-2 py-0.5 text-[11px] text-gray-600 dark:border-gray-700 dark:text-gray-300"
                  >
                    {eventLabel(key)}
                  </span>
                ))}
                {s.signalFilter.map((key) => (
                  <span
                    key={key}
                    className="rounded-full border border-indigo-200 px-2 py-0.5 text-[11px] text-indigo-600 dark:border-indigo-800 dark:text-indigo-300"
                  >
                    {signalLabel(key)}
                  </span>
                ))}
                {/* Visible on the card, not just in the create form: this one governs whether personal words
                    leave the platform, and there is no edit form to open and check it in. */}
                {s.includeFeedbackText && (
                  <span className="rounded-full border border-amber-300 px-2 py-0.5 text-[11px] text-amber-700 dark:border-amber-700/60 dark:text-amber-300">
                    {t("platformAutomationFeedbackTextOn")}
                  </span>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {created && (
        <div className="mb-3 rounded border border-amber-300 bg-amber-50 p-2 dark:border-amber-700/60 dark:bg-amber-900/20">
          <p className="text-xs font-medium text-amber-800 dark:text-amber-300">{t("automationSecretOnce")}</p>
          <div className="mt-1 flex items-center gap-2">
            <code className="flex-1 break-all rounded bg-white px-2 py-1 text-xs dark:bg-gray-900 dark:text-gray-100">
              {created.secret}
            </code>
            <button type="button" onClick={() => copy(created.secret)} className={btn}>
              {t("apiCopyToken")}
            </button>
          </div>
        </div>
      )}

      <div className="space-y-2">
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={t("automationDefaultName")}
          aria-label={t("automationDefaultName")}
          className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <input
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          aria-label={t("automationDestinationUrl")}
          placeholder="https://hooks.zapier.com/..."
          className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />

        <div>
          <span className="mb-1 block text-xs font-medium text-gray-600 dark:text-gray-300">
            {t("automationEventsHeading")}
          </span>
          <div className="space-y-1">
            {EVENTS.map((evt) => (
              <label key={evt.key} className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  aria-label={evt.label}
                  checked={selectedEvents[evt.key] ?? false}
                  onChange={(e) => setSelectedEvents((s) => ({ ...s, [evt.key]: e.target.checked }))}
                />
                {evt.label}
              </label>
            ))}
          </div>
        </div>

        {wantsFeedback && (
          <div className="rounded border border-amber-300 bg-amber-50 p-2 dark:border-amber-700/60 dark:bg-amber-900/20">
            <label className="flex items-start gap-2 text-sm">
              <input
                type="checkbox"
                className="mt-0.5"
                aria-label={t("platformAutomationIncludeFeedbackText")}
                checked={includeFeedbackText}
                onChange={(e) => setIncludeFeedbackText(e.target.checked)}
              />
              <span>
                <span className="text-gray-800 dark:text-gray-100">
                  {t("platformAutomationIncludeFeedbackText")}
                </span>
                <span className="mt-0.5 block text-xs text-gray-500 dark:text-gray-400">
                  {t("platformAutomationIncludeFeedbackTextHint")}
                </span>
              </span>
            </label>
          </div>
        )}

        <div>
          <span className="mb-1 block text-xs font-medium text-gray-600 dark:text-gray-300">
            {t("platformAutomationSignals")}
          </span>
          <div className="space-y-1">
            {signals.map((s) => (
              <label key={s.id} className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  aria-label={s.label}
                  checked={selectedSignals[s.id] ?? false}
                  onChange={(e) => setSelectedSignals((sel) => ({ ...sel, [s.id]: e.target.checked }))}
                />
                {`${s.label} (${s.key})`}
              </label>
            ))}
          </div>
        </div>

        <button
          type="button"
          onClick={onCreate}
          disabled={create.isPending || !url.trim() || eventTypes.length === 0}
          className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
        >
          {t("platformAutomationCreate")}
        </button>
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
