import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { useAuth } from "../auth";
import { bytesToGb, gbToBytes } from "../lib/format";

type Tab = "ai" | "quotas";

/// Settings modal with two tabs — AI (per-user summarisation/chat config) and, for the Platform
/// Administrator, Storage Quotas — saved together by a single OK/Cancel footer.
export default function SettingsModal({ onClose }: { onClose: () => void }) {
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
  const [contextWindow, setContextWindow] = useState("");

  // Storage quotas (GB inputs).
  const [starterGb, setStarterGb] = useState("");
  const [maxGb, setMaxGb] = useState("");

  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (data) {
      setApiBase(data.apiBase ?? "");
      setModel(data.model ?? "");
      setContextWindow(data.contextWindow != null ? String(data.contextWindow) : "");
    }
  }, [data]);

  useEffect(() => {
    if (platform) {
      setStarterGb(String(bytesToGb(platform.starterQuotaBytes)));
      setMaxGb(String(bytesToGb(platform.maxQuotaBytes)));
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
        contextWindow: contextWindow.trim() ? Number(contextWindow) : null,
      });
      qc.invalidateQueries({ queryKey: ["user-settings"] });

      if (isPlatformAdmin) {
        const starter = gbToBytes(Number(starterGb));
        const max = gbToBytes(Number(maxGb));
        if (!(starter > 0) || !(max > 0)) throw new Error("Quota values must be greater than zero.");
        if (starter > max) throw new Error("The starter quota can't exceed the maximum quota.");
        await api.updatePlatformSettings({ starterQuotaBytes: starter, maxQuotaBytes: max });
        qc.invalidateQueries({ queryKey: ["platform-settings"] });
      }
      onClose();
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label="Settings"
        className="flex max-h-[85vh] w-full max-w-md flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="border-b px-5 pt-4 dark:border-gray-700">
          <h2 className="mb-3 text-base font-semibold dark:text-gray-100">Settings</h2>
          {isPlatformAdmin && (
            <div className="flex gap-1" role="tablist" aria-label="Settings sections">
              <TabButton active={tab === "ai"} onClick={() => setTab("ai")}>
                AI Settings
              </TabButton>
              <TabButton active={tab === "quotas"} onClick={() => setTab("quotas")}>
                Storage Quotas
              </TabButton>
            </div>
          )}
        </div>

        {/* Non-login field names + per-field autoComplete="off" stop password managers autofilling these. */}
        <div className="min-h-0 flex-1 overflow-y-auto p-5">
          {tab === "ai" ? (
            <div className="space-y-3">
              <p className="text-xs text-gray-500 dark:text-gray-400">
                Your own OpenAI-compatible endpoint, used for both summaries and chat. Leave fields blank to
                use the server default.
              </p>
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">Summarisation endpoint (base URL)</span>
                <input
                  name="diariz-summary-endpoint"
                  autoComplete="off"
                  value={apiBase}
                  onChange={(e) => setApiBase(e.target.value)}
                  placeholder={data?.defaultApiBase ? `Default: ${data.defaultApiBase}` : "https://api.openai.com/v1"}
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
              </label>
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">Summarisation model</span>
                <input
                  name="diariz-summary-model"
                  autoComplete="off"
                  value={model}
                  onChange={(e) => setModel(e.target.value)}
                  placeholder={data?.defaultModel ? `Default: ${data.defaultModel}` : "gpt-4o-mini"}
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
              </label>
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">
                  Summarisation API key
                  {data?.hasApiKey && apiKey === null && (
                    <span className="ml-1 text-green-600 dark:text-green-400">· set</span>
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
                      ? "•••••• (leave blank to keep)"
                      : data?.serverHasApiKey
                        ? "Using server default (leave blank)"
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
                    Clear stored key
                  </button>
                )}
              </label>
              {apiKey === "" && (
                <p className="text-xs text-amber-600 dark:text-amber-400">The stored key will be cleared on save.</p>
              )}
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">Chat context window (tokens)</span>
                <input
                  type="number"
                  min={1}
                  name="diariz-chat-context"
                  autoComplete="off"
                  value={contextWindow}
                  onChange={(e) => setContextWindow(e.target.value)}
                  placeholder={data ? `Default: ${data.defaultContextWindow.toLocaleString()}` : "131072"}
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
                <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">
                  Drives the chat context dial. Leave blank to use the server default.
                </span>
              </label>
            </div>
          ) : (
            <div className="space-y-3">
              <p className="text-xs text-gray-500 dark:text-gray-400">
                New users start with the starter quota; administrators can raise a user up to the maximum.
              </p>
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">Starter quota (GB)</span>
                <input
                  type="number"
                  min={1}
                  step={0.5}
                  value={starterGb}
                  onChange={(e) => setStarterGb(e.target.value)}
                  aria-label="Starter quota (GB)"
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
              </label>
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">Maximum quota (GB)</span>
                <input
                  type="number"
                  min={1}
                  step={0.5}
                  value={maxGb}
                  onChange={(e) => setMaxGb(e.target.value)}
                  aria-label="Maximum quota (GB)"
                  className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
              </label>
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
