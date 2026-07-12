import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { useAuth } from "../auth";
import { api } from "../lib/api";
import Avatar from "./Avatar";
import ProfileSection from "./ProfileSection";
import AiSettingsSection from "./AiSettingsSection";
import ChatToolsSection from "./ChatToolsSection";
import RecordingsSection from "./RecordingsSection";
import GoogleAccountSection from "./GoogleAccountSection";
import CalendarFeedsSection from "./CalendarFeedsSection";
import McpAccessSection from "./McpAccessSection";
import DeveloperAccessSection from "./DeveloperAccessSection";
import VoicePrintsSection from "./VoicePrintsSection";

export type PreferencesTab =
  | "profile"
  | "ai"
  | "tools"
  | "recordings"
  | "google"
  | "feeds"
  | "claude"
  | "developers"
  | "voiceprints";

/// Personal preferences, organised as a vertical-tabbed modal (a left nav headed by the user's avatar/name,
/// with a content panel on the right). Each tab self-saves; the footer only closes. Sized to 80vw x 80vh
/// (the left nav is a fixed width, so the extra width goes to the right content panel - easier to read on
/// sub-4K monitors) and, like the Settings modal, does not close on a backdrop click (Escape still closes).
export default function PreferencesModal({
  onClose,
  initialTab = "profile",
}: {
  onClose: () => void;
  initialTab?: PreferencesTab;
}) {
  const { t } = useTranslation("account");
  const { initials, pictureUrl, fullName, email } = useAuth();
  const [tab, setTab] = useState<PreferencesTab>(initialTab);
  // The Developers tab appears only when a Platform Admin has enabled API access platform-wide.
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const tabs: { id: PreferencesTab; label: string }[] = [
    { id: "profile", label: t("tabProfile") },
    { id: "ai", label: t("aiSettings") },
    { id: "tools", label: t("chatToolsTab") },
    { id: "recordings", label: t("recordingsTab") },
    { id: "google", label: t("tabGoogleAccount") },
    { id: "feeds", label: t("tabCalendarFeeds") },
    { id: "claude", label: t("tabClaudeAccess") },
    ...(profile?.apiAccessEnabled ? [{ id: "developers" as const, label: t("tabDevelopers") }] : []),
    { id: "voiceprints", label: t("tabVoicePrints") },
  ];

  // The backdrop does NOT close on click (Close/Escape only) — prevents accidental dismissal mid-edit.
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-label={t("preferencesTitle")}
        className="flex h-[80vh] w-[80vw] min-w-[40rem] overflow-hidden rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        {/* Left nav: avatar + name, a separator, then the vertical tab list. */}
        <div className="flex w-56 shrink-0 flex-col border-r bg-gray-50 dark:border-gray-700 dark:bg-gray-950/40">
          <div className="flex flex-col items-center gap-2 px-4 py-5">
            <Avatar initials={initials} pictureUrl={pictureUrl} size="lg" />
            <div className="max-w-full truncate text-center text-sm font-medium dark:text-gray-100">
              {fullName ?? email}
            </div>
          </div>
          <div className="border-b dark:border-gray-700" />
          <nav className="flex flex-col gap-0.5 p-2" role="tablist" aria-label={t("preferencesTitle")}>
            {tabs.map((x) => (
              <button
                key={x.id}
                type="button"
                role="tab"
                aria-selected={tab === x.id}
                onClick={() => setTab(x.id)}
                className={`rounded px-3 py-2 text-left text-sm ${
                  tab === x.id
                    ? "bg-gray-900 font-medium text-white dark:bg-gray-100 dark:text-gray-900"
                    : "text-gray-700 hover:bg-gray-100 dark:text-gray-200 dark:hover:bg-gray-800"
                }`}
              >
                {x.label}
              </button>
            ))}
          </nav>
        </div>

        {/* Right content panel. */}
        <div className="flex min-w-0 flex-1 flex-col">
          <div className="border-b px-5 pt-4 pb-3 dark:border-gray-700">
            <h2 className="text-base font-semibold dark:text-gray-100">{t("preferencesTitle")}</h2>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto p-5">
            {tab === "profile" && <ProfileSection />}
            {tab === "ai" && <AiSettingsSection />}
            {tab === "tools" && <ChatToolsSection />}
            {tab === "recordings" && <RecordingsSection />}
            {tab === "google" && <GoogleAccountSection />}
            {tab === "feeds" && <CalendarFeedsSection />}
            {tab === "claude" && <McpAccessSection />}
            {tab === "developers" && <DeveloperAccessSection />}
            {tab === "voiceprints" && <VoicePrintsSection />}
          </div>
          <div className="flex items-center justify-end border-t px-5 py-3 dark:border-gray-700">
            <button
              type="button"
              onClick={onClose}
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("common:close")}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
