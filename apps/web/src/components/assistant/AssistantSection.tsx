import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { SearchIcon } from "../icons";
import HelpButton from "../HelpButton";
import SourceCard from "../SourceCard";
import type { UserSettings } from "../../lib/types";

const TOOLS_TINT = "#16a34a";
const TOOLS_TINT_DARK = "#4ade80";

/// The Preferences "Assistant" tab: what the assistant may look up on the user's behalf.
///
/// It used to carry a model card too. From 0.221.0 the model, endpoint, key, context window, timeout and
/// reasoning settings are platform-wide and live at /admin/llm-models - a user no longer chooses which
/// model answers them, so there is nothing here to state or change. Tools stay: which of their own
/// recordings the assistant may search is genuinely their decision.
export default function AssistantSection() {
  const { t } = useTranslation("account");
  const { data } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });

  // Render only once the settings have loaded, so an early edit can't be overwritten by the arriving values.
  if (!data) return null;

  return (
    <div>
      <div className="mb-3.5">
        <span className="block text-sm text-gray-700 dark:text-gray-300">{t("assistantTitle")}</span>
        <p className="mt-0.5 max-w-[560px] text-xs text-gray-400 dark:text-gray-500">{t("assistantIntro")}</p>
      </div>
      <div className="flex flex-col gap-3">
        <ChatToolsCard data={data} />
      </div>
    </div>
  );
}

/// The tool list: a master switch in the header, one line per tool in the body.
///
/// Saves on change rather than behind a Save button. Two Save buttons on one tab was part of what made
/// these read as two separate pages, and the tri-state write already only sends the tool fields, so it
/// cannot disturb the model override or the Recordings preferences.
function ChatToolsCard({ data }: { data: UserSettings }) {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const [error, setError] = useState<string | null>(null);

  // Mirrors the server until a write lands, so a click is reflected immediately rather than after a
  // round trip - and the query's own data is the source of truth on reload.
  const [enabled, setEnabled] = useState(data.toolsEnabled);
  const [states, setStates] = useState<Record<string, boolean>>(
    Object.fromEntries(data.tools.map((tool) => [tool.name, tool.enabled])),
  );

  async function save(next: { toolsEnabled: boolean; toolOverrides: Record<string, boolean> }) {
    setError(null);
    try {
      await api.updateUserSettings(next);
      qc.invalidateQueries({ queryKey: ["user-settings"] });
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  function toggleMaster(on: boolean) {
    setEnabled(on);
    void save({ toolsEnabled: on, toolOverrides: states });
  }

  function toggleTool(name: string, on: boolean) {
    const next = { ...states, [name]: on };
    setStates(next);
    void save({ toolsEnabled: enabled, toolOverrides: next });
  }

  return (
    <SourceCard
      name={t("assistantToolsName")}
      meta={t("assistantToolsMeta")}
      tint={TOOLS_TINT}
      tintDark={TOOLS_TINT_DARK}
      glyph={SearchIcon}
      help={<HelpButton topic="chat-over-transcripts" className="ml-1" />}
      error={error}
      actions={
        <label className="flex shrink-0 items-center gap-1.5 text-xs text-gray-600 dark:text-gray-400">
          <input type="checkbox" checked={enabled} onChange={(e) => toggleMaster(e.target.checked)} />
          {t("assistantToolsEnabled")}
        </label>
      }
    >
      <ul className={enabled ? "" : "opacity-50"}>
        {data.tools.map((tool) => (
          <li
            key={tool.name}
            // The server's own description is the instruction the model reads when deciding whether to
            // call this tool - a paragraph, and not UI copy. It lives in the tooltip; the line below is a
            // phrase written for a person.
            title={tool.description}
            className="flex items-center gap-2.5 border-b border-gray-100 py-[7px] last:border-b-0 dark:border-gray-800"
          >
            <input
              type="checkbox"
              aria-label={tool.title}
              disabled={!enabled}
              checked={states[tool.name] ?? tool.enabled}
              onChange={(e) => toggleTool(tool.name, e.target.checked)}
              className="shrink-0"
            />
            {/* A fixed column, not a grid: the descriptions line up, and the row still collapses
                gracefully when the modal is narrow. */}
            <span className="w-[150px] shrink-0 truncate text-[13px] text-gray-800 dark:text-gray-200">
              {tool.title}
            </span>
            <span className="min-w-0 flex-1 truncate text-[11px] text-gray-400 dark:text-gray-500">
              {toolPhrase(t, tool.name, tool.description)}
            </span>
          </li>
        ))}
      </ul>
    </SourceCard>
  );
}

/// A short phrase for a tool, written for a person. Falls back to the server's model-facing description
/// when a tool has no phrase yet, so a tool added server-side still reads as something rather than blank.
function toolPhrase(t: TFunction, name: string, fallback: string): string {
  return t(`toolPhrase_${name}`, { defaultValue: "" }) || fallback;
}
