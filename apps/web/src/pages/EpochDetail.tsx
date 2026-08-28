import { useState } from "react";
import { Link, Navigate, useParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { renderMarkdown } from "../lib/markdown";
import { useResizableWidth } from "../lib/useResizableWidth";
import { GITHUB_URL, TAGLINE } from "../lib/appInfo";
import { RECENT, type Release } from "../lib/releaseNotes";
import { EPOCHS } from "../lib/releaseNotes/epochs";
import { ARCHIVE } from "../lib/releaseNotes/archive";

/// One epoch's releases, verbatim: the list left, the selected release's notes right, as the release
/// notes page was before epochs existed.
///
/// **This is the only module allowed to import the release archive** (`bundleBoundary.test.ts` enforces
/// it), and it is reached only through a lazy route, so the archive lands in this chunk rather than in
/// everyone's initial bundle.
///
/// `current` is a reserved id for the open epoch - the releases in `current.ts` that have not been
/// closed into an epoch yet. `epochs.test.ts` keeps any real epoch from claiming that id.
export const OPEN_EPOCH_ID = "current";

function releasesIn(epochId: string | undefined, latestTitle: string): { title: string; releases: Release[] } | null {
  if (epochId === OPEN_EPOCH_ID) return { title: latestTitle, releases: RECENT };

  const epoch = EPOCHS.find((e) => e.id === epochId);
  if (!epoch) return null;

  // ARCHIVE runs newest first, so the epoch's newest release (`to`) is the start of the slice.
  const start = ARCHIVE.findIndex((r) => r.version === epoch.to);
  const end = ARCHIVE.findIndex((r) => r.version === epoch.from);
  if (start === -1 || end === -1) return null;

  return { title: epoch.title, releases: ARCHIVE.slice(start, end + 1) };
}

export default function EpochDetail() {
  const { epochId } = useParams();
  const { t } = useTranslation("account");
  // Every hook runs before the redirect below: bailing out earlier would skip the ones after it and
  // break the Rules of Hooks the moment an unknown id was routed to.
  const [selected, setSelected] = useState<string | null>(null);
  const { width, startResize } = useResizableWidth("diariz.releaseNotes.listWidth", {
    min: 220,
    max: 560,
    initial: 320,
  });

  const view = releasesIn(epochId, t("latestReleases"));
  if (!view) return <Navigate to="/release-notes" replace />;

  const release = view.releases.find((r) => r.version === selected) ?? view.releases[0];

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
        <Link to="/release-notes" className="ml-auto text-sm text-blue-600 hover:underline dark:text-blue-400">
          {t("allReleases")}
        </Link>
        <h1 className="text-base font-semibold text-gray-700 dark:text-gray-200">{view.title}</h1>
      </header>

      <div className="flex min-h-0 flex-1">
        {/* Left: this epoch's releases (independent scroll), drag-resizable via the separator below. */}
        <nav
          style={{ width }}
          className="shrink-0 overflow-y-auto border-r bg-white dark:border-gray-700 dark:bg-gray-900"
        >
          <ul className="divide-y dark:divide-gray-800">
            {view.releases.map((r) => (
              <li key={r.version}>
                <button
                  type="button"
                  onClick={() => setSelected(r.version)}
                  className={`block w-full px-4 py-2 text-left ${
                    r.version === release.version
                      ? "bg-blue-50 dark:bg-blue-900/30"
                      : "hover:bg-gray-50 dark:hover:bg-gray-800"
                  }`}
                >
                  <div className="flex items-baseline gap-2">
                    <span className="text-sm font-medium dark:text-gray-100">v{r.version}</span>
                    <span className="text-xs text-gray-500 dark:text-gray-400">{r.date}</span>
                  </div>
                  <div className="truncate text-xs text-gray-600 dark:text-gray-300">{r.headline}</div>
                </button>
              </li>
            ))}
          </ul>
        </nav>

        <div
          role="separator"
          aria-orientation="vertical"
          aria-label={t("resizeReleaseList")}
          onMouseDown={(e) => startResize(e, "left")}
          className="w-1 shrink-0 cursor-col-resize bg-transparent transition-colors hover:bg-blue-400 dark:hover:bg-blue-600"
        />

        {/* Right: selected release (independent scroll) */}
        <main className="min-w-0 flex-1 overflow-y-auto p-6">
          {release && <ReleaseDetail release={release} />}
        </main>
      </div>
    </div>
  );
}

function ReleaseDetail({ release }: { release: Release }) {
  const { t } = useTranslation("account");
  return (
    <article className="mx-auto max-w-3xl">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h2 className="text-xl font-semibold dark:text-gray-100">v{release.version}</h2>
        <span className="text-sm text-gray-500 dark:text-gray-400">{release.date}</span>
        {release.pr != null && (
          <a
            href={`${GITHUB_URL}/pull/${release.pr}`}
            target="_blank"
            rel="noopener noreferrer"
            className="text-sm text-blue-600 hover:underline dark:text-blue-400"
          >
            #{release.pr}
          </a>
        )}
      </div>
      <p className="mt-1 text-base font-medium text-gray-800 dark:text-gray-200">{release.headline}</p>

      <div
        className="chat-md mt-4 space-y-2 text-sm text-gray-700 dark:text-gray-300 [&_a]:text-blue-600 [&_a]:underline [&_code]:rounded [&_code]:bg-black/10 [&_code]:px-1 [&_strong]:font-semibold"
        dangerouslySetInnerHTML={{ __html: renderMarkdown(release.summary) }}
      />

      <ChangeList title={t("changeAdded")} items={release.added} />
      <ChangeList title={t("changeChanged")} items={release.changed} />
      <ChangeList title={t("changeFixed")} items={release.fixed} />
    </article>
  );
}

function ChangeList({ title, items }: { title: string; items?: string[] }) {
  if (!items || items.length === 0) return null;
  return (
    <section className="mt-4">
      <h3 className="text-xs font-bold uppercase tracking-wide text-gray-400">{title}</h3>
      <ul className="mt-1 list-disc space-y-1 pl-5 text-sm text-gray-700 dark:text-gray-300">
        {items.map((it, i) => (
          <li key={i}>{it}</li>
        ))}
      </ul>
    </section>
  );
}
