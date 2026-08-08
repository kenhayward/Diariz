import { useTranslation } from "react-i18next";
import McpCard from "./McpCard";
import ApiCard from "./ApiCard";
import AutomationsCard from "./AutomationsCard";

/// The Preferences "Integrations" tab: every way other software reaches Diariz, and every way Diariz
/// reaches out. It replaces three separate nav entries - Claude Access, Developers and Automations -
/// which were the same kind of thing, read by the same kind of person, and thin on their own.
///
/// Named for what it covers rather than for who reads it: "Developers" excluded the Zapier user who
/// writes no code, and it goes both directions - inbound over MCP and REST, outbound over webhooks.
///
/// Same shape as the Calendars panel, deliberately: `SourceCard` per source, each owning its own queries
/// so a provider that is failing degrades to one broken card rather than an empty page. Inbound first,
/// outbound last.
export default function IntegrationsSection() {
  const { t } = useTranslation("account");

  return (
    <div>
      <div className="mb-3.5">
        <span className="block text-sm text-gray-700 dark:text-gray-300">{t("integrationsTitle")}</span>
        <p className="mt-0.5 max-w-[560px] text-xs text-gray-400 dark:text-gray-500">{t("integrationsIntro")}</p>
      </div>
      <div className="flex flex-col gap-3">
        <McpCard />
        <ApiCard />
        <AutomationsCard />
      </div>
    </div>
  );
}
