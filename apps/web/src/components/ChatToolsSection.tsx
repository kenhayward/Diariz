import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";

/// Chat Tools tab: the master chat-tool-calling switch plus a per-tool on/off table. Self-contained; its Save
/// PUTs only the tool fields (tri-state), leaving the Model Settings and Recordings preferences untouched.
export default function ChatToolsSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });

  const [toolsEnabled, setToolsEnabled] = useState(false);
  const [toolStates, setToolStates] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (data) {
      setToolsEnabled(data.toolsEnabled);
      setToolStates(Object.fromEntries(data.tools.map((tool) => [tool.name, tool.enabled])));
    }
  }, [data]);

  // Render only once the settings have loaded, so an early edit can't be overwritten by the arriving values.
  if (!data) return null;

  async function onSave() {
    setError(null);
    setSaved(false);
    setBusy(true);
    try {
      await api.updateUserSettings({ toolsEnabled, toolOverrides: toolStates });
      qc.invalidateQueries({ queryKey: ["user-settings"] });
      setSaved(true);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-3">
      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={toolsEnabled} onChange={(e) => setToolsEnabled(e.target.checked)} />
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
