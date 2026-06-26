/// Fraction of the context window consumed (0–1), guarding against a zero/unknown total.
export function contextFraction(used: number, total: number): number {
  if (!(total > 0)) return 0;
  return Math.min(1, Math.max(0, used / total));
}

/// A small ring gauge showing how much of the model's context window is used, with a hover tooltip
/// ("model  used / total tokens (pct%)"). The token figures are estimates (~4 chars/token).
export default function ContextDial({
  model,
  used,
  total,
}: {
  model: string;
  used: number;
  total: number;
}) {
  const frac = contextFraction(used, total);
  const pct = Math.round(frac * 100);
  const r = 9;
  const circ = 2 * Math.PI * r;
  const danger = frac >= 0.9;

  return (
    <div
      className="group relative inline-flex items-center gap-1.5"
      aria-label={`Context ${pct}% used`}
    >
      <svg width="22" height="22" viewBox="0 0 26 26" aria-hidden="true" className="shrink-0">
        <circle cx="13" cy="13" r={r} fill="none" strokeWidth="3.5" className="stroke-gray-200 dark:stroke-gray-700" />
        <circle
          cx="13"
          cy="13"
          r={r}
          fill="none"
          strokeWidth="3.5"
          strokeLinecap="round"
          strokeDasharray={`${frac * circ} ${circ}`}
          transform="rotate(-90 13 13)"
          className={danger ? "stroke-red-500" : "stroke-blue-500"}
        />
      </svg>
      {/* Always-visible usage: used / total (pct%). */}
      <span className="whitespace-nowrap text-[11px] tabular-nums text-gray-500 dark:text-gray-400">
        {used.toLocaleString()} / {total.toLocaleString()} ({pct}%)
      </span>
      <div
        role="tooltip"
        className="pointer-events-none absolute bottom-full right-0 z-30 mb-2 hidden whitespace-nowrap rounded-md border bg-white px-2 py-1 text-[11px] leading-tight shadow-lg group-hover:block dark:border-gray-700 dark:bg-gray-800"
      >
        <div className="font-medium text-gray-700 dark:text-gray-200">{model || "model"}</div>
        <div className="text-gray-500 dark:text-gray-400">
          {used.toLocaleString()} / {total.toLocaleString()} tokens ({pct}%)
        </div>
      </div>
    </div>
  );
}
