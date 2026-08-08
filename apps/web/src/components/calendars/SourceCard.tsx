import type { ComponentType, ReactNode } from "react";

/// One calendar source in the Preferences Calendars panel: a header carrying the provider's identity and
/// its own actions, over whatever body that provider needs.
///
/// Every source is this same shape, which is the point - adding a fourth provider is one more card and
/// nothing else moves. When that fourth arrives, this is where collapse-to-header and a descriptor list
/// (`{ id, name, tint, glyph, status, actions, Body }`) belong; with three fixed sources a registry would
/// only be indirection.

/// The shared header-button style, previously copy-pasted into all three sections.
export const cardBtn =
  "shrink-0 rounded border px-2 py-0.5 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

/// The same button, for the one action that destroys something.
export const cardBtnDanger =
  "shrink-0 rounded border px-2 py-0.5 text-xs text-red-600 hover:bg-red-50 disabled:opacity-50 dark:border-gray-700 dark:text-red-400 dark:hover:bg-gray-800";

/// A hex colour as an `rgba()` string, so a provider's tint can wash its tile without a second token.
function tintBg(hex: string, alpha: number): string {
  const n = parseInt(hex.replace("#", ""), 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
}

export default function SourceCard({
  name,
  meta,
  tint,
  glyph: Glyph,
  status,
  actions,
  error,
  children,
}: {
  name: string;
  /// The account and counts under the name - what this source actually is, in one line.
  meta: string;
  /// The provider's colour, tinting the tile and its glyph.
  tint: string;
  glyph: ComponentType<{ size?: number; title?: string }>;
  /// Per-source vocabulary ("Connected", "Mirroring", "2 shown") - not one word forced on every provider.
  /// Omitted when the source has nothing to report.
  status?: string;
  /// This source's own header controls.
  actions?: ReactNode;
  /// A failure from this source's last action. Rendered here rather than panel-level: a shared banner
  /// could not say which source failed.
  error?: string | null;
  children: ReactNode;
}) {
  return (
    // A labelled region, so a screen reader can tell which source a "Shown" tick belongs to. Three
    // sections on separate tabs could reuse control names; in one stack they cannot.
    <section
      aria-label={name}
      className="overflow-hidden rounded-lg border border-gray-200 bg-white dark:border-gray-700 dark:bg-white/[0.03]"
    >
      <div className="flex items-center gap-2.5 border-b border-gray-100 bg-gray-50 px-3 py-2.5 dark:border-gray-800 dark:bg-transparent">
        <span
          data-testid="source-tile"
          className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded-md"
          style={{ background: tintBg(tint, 0.16), color: tint }}
        >
          <Glyph size={14} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="truncate text-[13px] font-semibold text-gray-900 dark:text-gray-100">{name}</div>
          <div className="truncate text-[11px] text-gray-500 dark:text-gray-400">{meta}</div>
        </div>
        {status && (
          <span
            data-testid="source-status"
            className="shrink-0 rounded px-1.5 text-[10px] font-medium text-green-800 bg-green-100 dark:bg-green-500/[0.14] dark:text-green-400"
          >
            {status}
          </span>
        )}
        {actions}
      </div>
      <div className="px-3 pb-3 pt-2.5">
        {children}
        {error && <p className="mt-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
      </div>
    </section>
  );
}
