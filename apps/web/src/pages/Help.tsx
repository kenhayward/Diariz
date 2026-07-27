import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useParams } from "react-router-dom";
import { renderMarkdown } from "../lib/markdown";
import { useResizableWidth } from "../lib/useResizableWidth";
import { HELP_INDEX, articleBody, articleTree, findArticle } from "../lib/help/content";
import { searchArticles } from "../lib/help/search";
import { TAGLINE } from "../lib/releases";

/// Public, new-tab-friendly help browser: a fixed header, a searchable article tree on the left, and the
/// selected article on the right. Deliberately the same shape as `/release-notes` so the two long-form
/// pages feel like one thing.
///
/// Selection is driven by the route rather than local state, because the contextual `?` popovers deep
/// link straight to `/help/<slug>`.
export default function Help() {
  const { t, i18n } = useTranslation("help");
  const { slug } = useParams();
  const [query, setQuery] = useState("");
  const { width, startResize } = useResizableWidth("diariz.help.listWidth", {
    min: 220,
    max: 560,
    initial: 300,
  });

  const locale = i18n.language;
  const tree = useMemo(() => articleTree(HELP_INDEX, locale), [locale]);
  const hits = useMemo(
    () => (query.trim() ? searchArticles(query, tree.flatMap((g) => g.articles)) : []),
    [query, tree],
  );

  // No slug means "open the help", so land on the first article rather than an empty pane.
  const fallback = tree[0]?.articles[0];
  const article = slug ? findArticle(HELP_INDEX, slug, locale) : fallback;

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
        {/* Not a heading: the article's own title is the page's h1, so the chrome must not compete. */}
        <span className="ml-auto text-base font-semibold text-gray-700 dark:text-gray-200">{t("title")}</span>
        <Link to="/" className="text-sm text-indigo-600 hover:underline dark:text-indigo-400">
          {t("backToApp")}
        </Link>
      </header>

      <div className="flex min-h-0 flex-1">
        <nav
          style={{ width }}
          className="shrink-0 overflow-y-auto border-r bg-white dark:border-gray-700 dark:bg-gray-900"
        >
          <div className="border-b p-3 dark:border-gray-800">
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder={t("searchPlaceholder")}
              aria-label={t("searchLabel")}
              className="w-full rounded border px-2 py-1.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </div>

          {query.trim() ? (
            <SearchResults hits={hits} query={query} />
          ) : (
            <ul className="py-1">
              {tree.map((group) => (
                <li key={group.id}>
                  <h2 className="px-4 pb-1 pt-3 text-xs font-bold uppercase tracking-wide text-gray-400">
                    {t(group.label)}
                  </h2>
                  <ul>
                    {group.articles.map((a) => (
                      <li key={a.slug}>
                        <ArticleLink slug={a.slug} title={a.title} active={a.slug === article?.slug} />
                      </li>
                    ))}
                  </ul>
                </li>
              ))}
            </ul>
          )}
        </nav>

        <div
          role="separator"
          aria-orientation="vertical"
          aria-label={t("resizeList")}
          onMouseDown={(e) => startResize(e, "left")}
          className="w-1 shrink-0 cursor-col-resize bg-transparent transition-colors hover:bg-blue-400 dark:hover:bg-blue-600"
        />

        <main className="min-w-0 flex-1 overflow-y-auto p-6">
          {article ? (
            <article className="mx-auto max-w-3xl">
              <h1 className="text-2xl font-semibold dark:text-gray-100">{article.title}</h1>
              <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">{article.summary}</p>
              <div
                // `[&_img]:...` keeps a screenshot inside the column however large the PNG is - authors
                // paste captures at whatever size their display produced.
                className="chat-md mt-6 text-sm text-gray-700 dark:text-gray-300 [&_a]:text-blue-600 [&_a]:underline [&_blockquote]:border-l-2 [&_blockquote]:pl-3 [&_blockquote]:text-gray-500 [&_code]:rounded [&_code]:bg-black/10 [&_code]:px-1 [&_h2]:mb-2 [&_h2]:mt-6 [&_h2]:text-lg [&_h2]:font-semibold [&_h3]:mb-1 [&_h3]:mt-4 [&_h3]:text-base [&_h3]:font-semibold [&_img]:my-3 [&_img]:max-w-full [&_img]:rounded-lg [&_img]:border [&_img]:shadow-sm dark:[&_img]:border-gray-700 [&_li]:ml-5 [&_li]:list-disc [&_ol_li]:list-decimal [&_p]:mb-3 [&_pre]:overflow-x-auto [&_pre]:rounded [&_pre]:bg-black/10 [&_pre]:p-3 [&_strong]:font-semibold [&_ul]:mb-3"
                // Authored prose, hard-wrapped in the source: standard paragraph semantics, not chat's
                // one-newline-is-a-line-break.
                dangerouslySetInnerHTML={{ __html: renderMarkdown(articleBody(article), { breaks: false }) }}
              />
            </article>
          ) : (
            <div className="mx-auto max-w-3xl">
              <h1 className="text-2xl font-semibold dark:text-gray-100">{t("notFoundTitle")}</h1>
              <p className="mt-2 text-sm text-gray-600 dark:text-gray-300">{t("notFoundBody")}</p>
            </div>
          )}
        </main>
      </div>
    </div>
  );
}

function ArticleLink({ slug, title, active }: { slug: string; title: string; active: boolean }) {
  return (
    <Link
      to={`/help/${slug}`}
      className={`block px-4 py-1.5 text-sm ${
        active
          ? "bg-blue-50 font-medium dark:bg-blue-900/30 dark:text-gray-100"
          : "text-gray-700 hover:bg-gray-50 dark:text-gray-300 dark:hover:bg-gray-800"
      }`}
    >
      {title}
    </Link>
  );
}

function SearchResults({
  hits,
  query,
}: {
  hits: ReturnType<typeof searchArticles>;
  query: string;
}) {
  const { t } = useTranslation("help");

  if (hits.length === 0) {
    return <p className="px-4 py-3 text-sm text-gray-500 dark:text-gray-400">{t("noResults", { query })}</p>;
  }

  return (
    <>
      <p className="px-4 pb-1 pt-3 text-xs text-gray-400">{t("resultCount", { count: hits.length })}</p>
      <ul className="pb-2">
        {hits.map(({ article, snippet }) => (
          <li key={article.slug}>
            <Link
              to={`/help/${article.slug}`}
              className="block px-4 py-2 hover:bg-gray-50 dark:hover:bg-gray-800"
            >
              <div className="text-sm font-medium text-gray-800 dark:text-gray-100">{article.title}</div>
              <div className="line-clamp-2 text-xs text-gray-500 dark:text-gray-400">{snippet}</div>
            </Link>
          </li>
        ))}
      </ul>
    </>
  );
}
