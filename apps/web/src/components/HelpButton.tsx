import { useRef } from "react";
import { useTranslation } from "react-i18next";
import { HelpCircleIcon } from "./icons";
import { useHelp } from "../lib/help/HelpContext";
import { HELP_INDEX, findArticle } from "../lib/help/content";

/// The `?` placed next to a feature or a setting. Clicking it opens a short explanation with a link to
/// the full article.
///
/// `topic` is a help article slug. An unknown slug renders nothing rather than a dead button - and the
/// content gate (`content/help/helpContent.test.ts`) scans the source for these props, so a slug that
/// never resolves fails the build instead of shipping.
export default function HelpButton({ topic, className = "" }: { topic: string; className?: string }) {
  const { t, i18n } = useTranslation("help");
  const { openHelp } = useHelp();
  const ref = useRef<HTMLButtonElement>(null);
  const article = findArticle(HELP_INDEX, topic, i18n.language);

  if (!article) return null;

  const label = t("helpAbout", { topic: article.title });

  return (
    <button
      ref={ref}
      type="button"
      // The popover is positioned against the button's own box, measured at click time so it is correct
      // after any scrolling or panel resize.
      onClick={() => openHelp(topic, ref.current!.getBoundingClientRect())}
      aria-label={label}
      title={label}
      className={`inline-flex shrink-0 items-center justify-center rounded-full p-0.5 align-middle text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600 dark:text-gray-500 dark:hover:bg-gray-800 dark:hover:text-gray-300 ${className}`}
    >
      <HelpCircleIcon size={14} />
    </button>
  );
}
