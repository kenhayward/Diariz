import { useState } from "react";
import { useTranslation } from "react-i18next";
import PersonProfileTab from "./PersonProfileTab";
import PersonVoiceprintTab from "./PersonVoiceprintTab";
import type { Person } from "../lib/types";

/// One person, under two tabs: who they are, and what their voiceprint was trained on.
///
/// **Profile is the default** because the common task is fixing a job title while reading a transcript.
/// Voiceprint is an audit surface - you go looking for it, usually because recognition has drifted.
///
/// The shell owns nothing but which tab is showing. Both tabs read `person` from the caller, so switching
/// tabs cannot discard a half-typed edit in the other one, and neither tab has to know the other exists.
export default function PersonEditor({
  person,
  canManagePeople,
  onClose,
  onSaved,
  showDestructiveActions = true,
}: {
  person: Person;
  canManagePeople: boolean;
  onClose: () => void;
  /// Called after a successful save. The directory ignores it - you carry on editing the next person - but a
  /// caller editing one person alone uses it to close, and to refresh whatever was showing those details.
  onSaved?: () => void;
  /// Erasing a voiceprint and deleting a person are directory-scale acts with no undo. A caller focused on
  /// one person's contact details turns them off, so they are not sitting beside a job title waiting for a
  /// mis-click.
  showDestructiveActions?: boolean;
}) {
  const { t } = useTranslation("people");
  const [tab, setTab] = useState<"profile" | "voiceprint">("profile");
  // Which tabs have ever been opened, so the voiceprint's fetch is deferred until it is actually wanted.
  const [opened, setOpened] = useState({ profile: true, voiceprint: false });

  function show(key: "profile" | "voiceprint") {
    setTab(key);
    setOpened((o) => ({ ...o, [key]: true }));
  }

  const tabButton = (key: "profile" | "voiceprint", label: string) => (
    <button
      key={key}
      type="button"
      role="tab"
      aria-selected={tab === key}
      onClick={() => show(key)}
      className={`border-b-2 px-3 py-2 text-sm ${
        tab === key
          ? "border-gray-900 font-medium text-gray-900 dark:border-gray-100 dark:text-gray-50"
          : "border-transparent text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200"
      }`}
    >
      {label}
    </button>
  );

  return (
    <div className="flex h-full flex-col">
      <div role="tablist" className="flex shrink-0 gap-1 border-b px-2 dark:border-gray-700">
        {tabButton("profile", t("tabProfile"))}
        {tabButton("voiceprint", t("tabVoiceprint"))}
      </div>

      {/* Hidden, not unmounted. The Profile tab holds a local draft, and losing a half-typed correction
          to a name by glancing at the voiceprint is exactly the kind of thing nobody reports as a bug -
          the same reasoning that stops EditPersonModal closing on a stray backdrop click. */}
      <div
        role="tabpanel"
        data-testid="profile-panel"
        aria-label={t("tabProfile")}
        hidden={tab !== "profile"}
        className="min-h-0 flex-1"
      >
        <PersonProfileTab
          person={person}
          canManagePeople={canManagePeople}
          onClose={onClose}
          onSaved={onSaved}
          showDestructiveActions={showDestructiveActions}
        />
      </div>

      {/* Mounted on first open, then kept. Rendering it up front would fetch a person's training data for
          every row you click through in the directory. */}
      {opened.voiceprint && (
        <div
          role="tabpanel"
          data-testid="voiceprint-panel"
          aria-label={t("tabVoiceprint")}
          hidden={tab !== "voiceprint"}
          className="min-h-0 flex-1"
        >
          <PersonVoiceprintTab person={person} />
        </div>
      )}
    </div>
  );
}
