import { useTranslation } from "react-i18next";
import GoogleCalendarCard from "./GoogleCalendarCard";
import OutlookCard from "./OutlookCard";
import FeedsCard from "./FeedsCard";

/// The Preferences "Calendars" tab: everything that feeds the Calendar tab and matches recordings to
/// meetings, in one place. It replaces three separate nav entries - Google Account, Calendar Feeds and
/// Outlook - which between them made "what feeds my Calendar tab?" a question you had to visit three
/// pages and reconcile three layouts to answer.
///
/// The stack holds no state of its own: every card owns its own queries and renders its own errors, so a
/// provider that is failing degrades to one broken card rather than an empty panel.
///
/// Deliberately three hard-coded cards rather than a descriptor registry. With a fixed set there is
/// nothing to choose between, and the shape that actually makes a fourth provider cheap is `SourceCard`.
/// When a fourth arrives it brings the panel-level "+ Add calendar source" menu and collapse-to-header
/// with it, and *that* is the point to introduce a descriptor list - designing one now would only have to
/// be redesigned then.
export default function CalendarsSection() {
  const { t } = useTranslation("account");

  return (
    <div>
      <div className="mb-3.5">
        <span className="block text-sm text-gray-700 dark:text-gray-300">{t("calendarsTitle")}</span>
        <p className="mt-0.5 max-w-[520px] text-xs text-gray-400 dark:text-gray-500">{t("calendarsIntro")}</p>
      </div>
      <div className="flex flex-col gap-3">
        <GoogleCalendarCard />
        <OutlookCard />
        <FeedsCard />
      </div>
    </div>
  );
}
