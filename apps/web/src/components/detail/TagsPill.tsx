import { useTranslation } from "react-i18next";
import { TagIcon } from "../icons";

/// How many tags the hover text names before it summarises the rest.
const NAMED_IN_TITLE = 4;

/// The hero card's tag control: a count, and the way into the tag popover. Presentational - the parent owns
/// whether the popover is open, and all the tag data comes in as props, so this file has no state at all.
/// Task 11 renders it (and owns `open`); Task 12 owns the tag data and the server round-trip.
export default function TagsPill({
  count,
  tags,
  open,
  onToggle,
}: {
  count: number;
  tags: string[];
  open: boolean;
  onToggle: () => void;
}) {
  const { t } = useTranslation(["workspace"]);

  /// Hover text: name the first few tags, then say how many more there are. With none, invite a first one.
  const title =
    tags.length === 0
      ? t("workspace:tagsPillEmptyTitle")
      : [
          ...tags.slice(0, NAMED_IN_TITLE),
          ...(tags.length > NAMED_IN_TITLE
            ? [t("workspace:tagsPillMore", { count: tags.length - NAMED_IN_TITLE })]
            : []),
        ].join(" · ");

  return (
    <button
      type="button"
      onClick={onToggle}
      title={title}
      aria-label={t("workspace:tagsPillLabel")}
      aria-haspopup="dialog"
      aria-expanded={open}
      className="hub-tags-pill inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-semibold transition-colors"
      style={{
        border: "1px solid rgba(47,107,237,.45)",
        background: "rgba(47,107,237,.16)",
        color: "var(--hub-blue-text)",
      }}
    >
      <TagIcon size={14} />
      <span>{t("workspace:tagsPillLabel")}</span>
      <span className="font-medium" style={{ color: "var(--hub-muted)" }}>
        {count}
      </span>
      <svg
        width={12}
        height={12}
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth={2}
        strokeLinecap="round"
        strokeLinejoin="round"
        style={{ color: "var(--hub-muted)" }}
        aria-hidden="true"
      >
        <path d="m6 9 6 6 6-6" />
      </svg>
    </button>
  );
}
