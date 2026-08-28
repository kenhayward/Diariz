import { Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { renderMarkdown } from "../lib/markdown";
import { TAGLINE } from "../lib/appInfo";
import { RECENT } from "../lib/releaseNotes";
import { EPOCHS, ARCHIVED_SPINE } from "../lib/releaseNotes/epochs";
import { epochSpan } from "../lib/releaseNotes/epochSpan";

/// Public page (new-tab friendly): the release history as epochs, newest first, each drilling into its
/// own page for the individual releases.
///
/// This used to be a flat list of every release ever shipped, which by 496 of them was both a wall of
/// undifferentiated rows and ~170 KB gzip in everyone's initial bundle. The epochs are a readable
/// default; the entries behind them are all still there, one click away.
///
/// Note what this module does *not* import: `releaseNotes/archive`. The counts and date spans come from
/// `ARCHIVED_SPINE`, precisely so that opening the summary view does not pull in the history it
/// summarises.
export default function ReleaseNotes() {
  const { t } = useTranslation("account");

  const oldestCurrent = RECENT[RECENT.length - 1];
  const newestCurrent = RECENT[0];

  return (
    <div className="flex h-screen flex-col bg-gray-50 dark:bg-gray-950">
      <header className="flex shrink-0 items-center gap-3 border-b bg-white px-6 py-3 dark:border-gray-700 dark:bg-gray-900">
        <img src="/logo.png" alt="" className="h-8 w-auto" />
        <div className="min-w-0">
          <div className="flex items-baseline gap-2">
            <span className="text-lg font-semibold dark:text-gray-100">Diariz</span>
            <span className="truncate text-sm text-gray-500 dark:text-gray-400">{TAGLINE}</span>
          </div>
        </div>
        <h1 className="ml-auto text-base font-semibold text-gray-700 dark:text-gray-200">{t("releaseNotes")}</h1>
      </header>

      <main className="min-h-0 flex-1 overflow-y-auto p-6">
        <div className="mx-auto flex max-w-3xl flex-col gap-3">
          {/* The open epoch: releases that have not been closed into one yet. */}
          <EpochCard
            to="/release-notes/current"
            title={t("latestReleases")}
            versions={`v${oldestCurrent.version} - v${newestCurrent.version}`}
            count={RECENT.length}
            earliest={oldestCurrent.date}
            latest={newestCurrent.date}
          >
            <p className="text-sm text-gray-700 dark:text-gray-300">{newestCurrent.headline}</p>
          </EpochCard>

          {EPOCHS.map((e) => {
            const span = epochSpan(e, ARCHIVED_SPINE);
            return (
              <EpochCard
                key={e.id}
                to={`/release-notes/${e.id}`}
                title={e.title}
                versions={`v${e.from} - v${e.to}`}
                count={span?.count ?? 0}
                earliest={span?.earliest ?? ""}
                latest={span?.latest ?? ""}
              >
                <div
                  className="chat-md space-y-2 text-sm text-gray-700 dark:text-gray-300 [&_code]:rounded [&_code]:bg-black/10 [&_code]:px-1 [&_strong]:font-semibold"
                  dangerouslySetInnerHTML={{ __html: renderMarkdown(e.summary) }}
                />
              </EpochCard>
            );
          })}
        </div>
      </main>
    </div>
  );
}

function EpochCard({
  to,
  title,
  versions,
  count,
  earliest,
  latest,
  children,
}: {
  to: string;
  title: string;
  versions: string;
  count: number;
  earliest: string;
  latest: string;
  children: React.ReactNode;
}) {
  const { t } = useTranslation("account");
  return (
    <Link
      to={to}
      className="block rounded-lg border bg-white p-5 transition-colors hover:border-blue-400 dark:border-gray-700 dark:bg-gray-900 dark:hover:border-blue-600"
    >
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h2 className="text-lg font-semibold dark:text-gray-100">{title}</h2>
        <span className="text-sm text-gray-500 dark:text-gray-400">{versions}</span>
      </div>
      <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">
        <span>{t("epochReleaseCount", { count })}</span>
        {earliest !== "" && <span>{` · ${earliest} - ${latest}`}</span>}
      </p>
      <div className="mt-3">{children}</div>
    </Link>
  );
}
