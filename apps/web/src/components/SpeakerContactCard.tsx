import { useTranslation } from "react-i18next";
import type { SpeakerInfo } from "../lib/types";

/// Whether we know enough about this speaker to say anything about them as a person.
///
/// An anonymous speaker and a "Multiple Speakers" slot are not one person - the server sends null for every
/// detail on both - and a speaker whose person record carries nothing but a name has nothing to show either.
function hasDetails(info: SpeakerInfo): boolean {
  if (!info.personId || info.isMultiSpeaker) return false;
  return Boolean(info.title || info.companyName || info.email || info.phone || info.isInternal !== null);
}

/// The person, as plain lines of text. Used for the Speakers-tab chip tooltip as well as the card below, so
/// the two cannot end up describing the same person differently.
export function contactSummary(info: SpeakerInfo, labels?: { internal: string; external: string }): string {
  return [
    info.displayName,
    info.title,
    info.companyName,
    info.email,
    info.phone,
    info.isInternal === null ? null : info.isInternal ? (labels?.internal ?? "Internal") : (labels?.external ?? "External"),
  ]
    .filter(Boolean)
    .join("\n");
}

/// A contact card for the speaker whose segments are open.
///
/// This is the only place inside a transcript where a person's email address and phone number are reachable -
/// the Speakers row has room for a job title and a company and no more. Both are links rather than text,
/// because the reason to look someone up mid-transcript is almost always to contact them.
export default function SpeakerContactCard({ info }: { info: SpeakerInfo }) {
  const { t } = useTranslation(["workspace", "people"]);

  if (!hasDetails(info)) return null;

  const role = [info.title, info.companyName].filter(Boolean).join(", ");
  // tel: does not tolerate the spaces a readable phone number is written with, so the href is stripped while
  // the label keeps the formatting the user typed.
  const dial = info.phone?.replace(/[\s()-]/g, "");

  return (
    <div className="mb-3 rounded-lg border bg-gray-50 px-3 py-2 dark:border-gray-700 dark:bg-gray-800/50">
      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <span className="text-sm font-medium text-gray-900 dark:text-gray-100">{info.displayName}</span>
        {role && <span className="text-xs text-gray-500 dark:text-gray-400">{role}</span>}
        {info.isInternal !== null && (
          <span
            className={`rounded px-1 text-[10px] font-medium ${
              info.isInternal
                ? "bg-gray-200 text-gray-700 dark:bg-gray-700 dark:text-gray-200"
                : "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"
            }`}
          >
            {info.isInternal ? t("workspace:speakerInternal") : t("workspace:speakerExternal")}
          </span>
        )}
      </div>

      {(info.email || info.phone) && (
        <div className="mt-1 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs">
          {info.email && (
            <a href={`mailto:${info.email}`} className="text-blue-600 hover:underline dark:text-blue-400">
              {info.email}
            </a>
          )}
          {info.phone && (
            <a href={`tel:${dial}`} className="text-blue-600 hover:underline dark:text-blue-400">
              {info.phone}
            </a>
          )}
        </div>
      )}
    </div>
  );
}
