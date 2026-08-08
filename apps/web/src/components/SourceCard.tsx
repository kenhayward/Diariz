import type { ComponentType, CSSProperties, ReactNode } from "react";

/// One source in a Preferences panel: a header carrying the thing's identity and its own actions, over
/// whatever body it needs. Shared by the Calendars panel (a calendar provider per card) and the
/// Integrations page (a way in or out of Diariz per card).
///
/// Every source is this same shape, which is the point - adding another is one more card and nothing else
/// moves. When a panel outgrows a hard-coded stack, this is where collapse-to-header and a descriptor list
/// (`{ id, name, tint, glyph, status, actions, Body }`) belong; at three or four fixed sources a registry
/// would only be indirection.

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
  tintDark,
  glyph: Glyph,
  status,
  statusTone = "positive",
  actions,
  help,
  error,
  disabledNote,
  children,
}: {
  name: string;
  /// The account and counts under the name - what this source actually is, in one line.
  meta: string;
  /// The provider's colour, tinting the tile and its glyph.
  tint: string;
  /// The same colour lifted for dark mode. A mid-tone brand colour carries across both themes, but a grey
  /// or a deep amber goes muddy on a dark surface - those pass a lighter value here.
  tintDark?: string;
  glyph: ComponentType<{ size?: number; title?: string }>;
  /// Per-source vocabulary ("Connected", "Mirroring", "2 shown") - not one word forced on every provider.
  /// Omitted when the source has nothing to report.
  status?: string;
  /// Green reads as "this is on and working". A chip that is merely stating which of two normal states
  /// something is in ("Platform default" / "Overridden") is information, not good news, so it goes grey.
  statusTone?: "positive" | "muted";
  /// This source's own header controls.
  actions?: ReactNode;
  /// A contextual `?` sitting after the meta line, where the old sections put it after their intro.
  help?: ReactNode;
  /// A failure from this source's last action. Rendered here rather than panel-level: a shared banner
  /// could not say which source failed.
  error?: string | null;
  /// Switched off platform-wide: the body is replaced by this line and the header actions are dropped.
  /// Shown rather than hidden so the capability stays discoverable - a missing card reads as a bug, and
  /// "ask an administrator" is a better answer than nothing at all.
  disabledNote?: string | null;
  /// Null for a card whose whole content is its header - it then renders no body at all.
  children?: ReactNode;
}) {
  return (
    // A labelled region, so a screen reader can tell which source a "Shown" tick belongs to. Three
    // sections on separate tabs could reuse control names; in one stack they cannot.
    <section
      aria-label={name}
      className="overflow-hidden rounded-lg border border-gray-200 bg-white dark:border-gray-700 dark:bg-white/[0.03]"
    >
      <div className="flex items-center gap-2.5 border-b border-gray-100 bg-gray-50 px-3 py-2.5 dark:border-gray-800 dark:bg-transparent">
        {/* The dark variant rides on CSS custom properties rather than two elements, so the glyph inherits
            whichever is live via `currentColor`. */}
        <span
          data-testid="source-tile"
          className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded-md bg-(--tile-bg) text-(--tile-fg) dark:bg-(--tile-bg-dark) dark:text-(--tile-fg-dark)"
          style={
            {
              "--tile-bg": tintBg(tint, 0.16),
              "--tile-fg": tint,
              "--tile-bg-dark": tintBg(tintDark ?? tint, 0.16),
              "--tile-fg-dark": tintDark ?? tint,
            } as CSSProperties
          }
        >
          <Glyph size={14} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="truncate text-[13px] font-semibold text-gray-900 dark:text-gray-100">{name}</div>
          <div className="truncate text-[11px] text-gray-500 dark:text-gray-400">
            {meta}
            {help}
          </div>
        </div>
        {status && !disabledNote && (
          <span
            data-testid="source-status"
            className={`shrink-0 rounded px-1.5 text-[10px] font-medium ${
              statusTone === "muted"
                ? "bg-gray-100 text-gray-500 dark:bg-slate-400/[0.16] dark:text-slate-300"
                : "bg-green-100 text-green-800 dark:bg-green-500/[0.14] dark:text-green-400"
            }`}
          >
            {status}
          </span>
        )}
        {/* An action that cannot work is worse than no action, so a switched-off source keeps its header
            but loses its buttons. */}
        {!disabledNote && actions}
      </div>
      {(children != null || disabledNote || error) && (
      <div className="px-3 pb-3 pt-2.5">
        {disabledNote ? (
          <p className="text-[13px] text-gray-500 dark:text-gray-400">{disabledNote}</p>
        ) : (
          <>
            {children}
            {error && <p className="mt-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
          </>
        )}
      </div>
      )}
    </section>
  );
}
