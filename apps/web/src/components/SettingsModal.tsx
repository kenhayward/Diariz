import { useEffect, useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";

/// Per-user summarisation settings. The API key is write-only: the server returns only whether one
/// is set, so an untouched field leaves it unchanged.
export default function SettingsModal({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient();
  const { data } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });

  const [apiBase, setApiBase] = useState("");
  const [model, setModel] = useState("");
  const [apiKey, setApiKey] = useState<string | null>(null); // null = untouched, "" = clear, value = set
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (data) {
      setApiBase(data.apiBase ?? "");
      setModel(data.model ?? "");
    }
  }, [data]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const save = useMutation({
    mutationFn: () =>
      api.updateUserSettings({
        apiBase: apiBase.trim() || null,
        model: model.trim() || null,
        apiKey, // null leaves it unchanged; "" clears; a value sets it
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["user-settings"] });
      onClose();
    },
    onError: (e) => setError(apiErrorMessage(e)),
  });

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
      onClick={onClose}
    >
      <div
        role="dialog"
        aria-label="Settings"
        className="w-full max-w-md rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-1 text-base font-semibold dark:text-gray-100">Summarisation settings</h2>
        <p className="mb-4 text-xs text-gray-500 dark:text-gray-400">
          Your own OpenAI-compatible endpoint for summaries. Leave fields blank to use the server default.
        </p>
        {/* autoComplete="off" + non-login field names stop password managers treating these as
            username/password login fields and autofilling stored credentials. */}
        <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} className="space-y-3" autoComplete="off">
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
          {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}
          <div className="flex justify-end gap-2 pt-1">
            <button
              type="button"
              onClick={onClose}
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={save.isPending}
              className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
            >
              {save.isPending ? "Saving…" : "Save"}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
