import type { ReactNode } from "react";

/// One of the hero card's high-level detail chips (date, duration, audio + retention, language,
/// speakers, recorded-by, rooms). A bordered pill with a colour-tinted leading icon — the tint is what
/// makes the row scannable, so each chip names the tone it wants rather than inheriting one.

const TONES = {
  cyan: "text-sky-600 dark:text-sky-400",
  green: "text-emerald-600 dark:text-emerald-400",
  amber: "text-amber-600 dark:text-amber-400",
  purple: "text-violet-600 dark:text-violet-400",
  pink: "text-pink-600 dark:text-pink-400",
  muted: "text-gray-500 dark:text-gray-400",
} as const;

export type ChipTone = keyof typeof TONES;

export default function DetailChip({
  icon,
  tone = "muted",
  children,
}: {
  icon?: ReactNode;
  tone?: ChipTone;
  children: ReactNode;
}) {
  return (
    <span className="inline-flex items-center gap-1.5 rounded-lg border border-gray-200 bg-gray-50 px-2.5 py-1 text-xs text-gray-700 dark:border-gray-700 dark:bg-gray-900 dark:text-gray-300">
      {icon && <span className={`flex shrink-0 ${TONES[tone]}`}>{icon}</span>}
      {children}
    </span>
  );
}
