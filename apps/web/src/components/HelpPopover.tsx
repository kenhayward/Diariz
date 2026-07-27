import { useEffect } from "react";
import { createPortal } from "react-dom";
import { useTranslation } from "react-i18next";
import { renderMarkdown } from "../lib/markdown";
import { HELP_INDEX, findArticle } from "../lib/help/content";

/** Panel width in px. Wide enough for two or three sentences, narrow enough to sit beside a field. */
const WIDTH = 320;
/** Gap between the `?` button and the panel. */
const GAP = 8;
/** Keeps the panel off the very edge of the viewport when it has to be nudged back inside. */
const MARGIN = 8;

/// The contextual-help panel. Rendered once by `HelpProvider`, never by the `?` button itself, so it
/// portals out of any modal it was triggered from.
///
/// It shows the article's own `summary` field rather than a separately authored blurb, so the short and
/// long forms of an explanation cannot drift apart.
export default function HelpPopover({
  topic,
  anchor,
  onClose,
}: {
  topic: string;
  anchor: DOMRect;
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation("help");
  const article = findArticle(HELP_INDEX, topic, i18n.language);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  if (!article) return null;

  // Sit below the button by default, flipping above when there is no room; nudge horizontally so the
  // panel never hangs off screen for a `?` near the right edge.
  const below = anchor.bottom + GAP;
  const flipUp = below + 200 > window.innerHeight && anchor.top > 200;
  const left = Math.max(MARGIN, Math.min(anchor.left, window.innerWidth - WIDTH - MARGIN));

  return createPortal(
    <>
      {/* Above modals (z-50) and the guided-tour overlay (z-[60]). */}
      <div data-testid="help-popover-backdrop" onClick={onClose} className="fixed inset-0 z-[70]" />
      <div
        role="dialog"
        aria-label={t("helpAbout", { topic: article.title })}
        className="fixed z-[71] rounded-xl border bg-white p-4 shadow-2xl dark:border-gray-700 dark:bg-gray-900"
        style={{
          width: WIDTH,
          left,
          ...(flipUp ? { bottom: window.innerHeight - anchor.top + GAP } : { top: below }),
        }}
      >
        <h3 className="text-sm font-semibold dark:text-gray-100">{article.title}</h3>
        <div
          className="chat-md mt-1 text-sm text-gray-600 dark:text-gray-300 [&_a]:text-blue-600 [&_a]:underline [&_strong]:font-semibold"
          dangerouslySetInnerHTML={{ __html: renderMarkdown(article.summary) }}
        />
        <div className="mt-3 flex items-center justify-between">
          <a
            // A new tab, so reading the full article never discards unsaved state in the modal the `?`
            // was clicked from.
            href={`/help/${article.slug}`}
            target="_blank"
            rel="noopener noreferrer"
            className="text-xs text-blue-600 hover:underline dark:text-blue-400"
          >
            {t("readMore")}
          </a>
          <button
            type="button"
            onClick={onClose}
            className="text-xs text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
          >
            {t("common:close")}
          </button>
        </div>
      </div>
    </>,
    document.body,
  );
}
