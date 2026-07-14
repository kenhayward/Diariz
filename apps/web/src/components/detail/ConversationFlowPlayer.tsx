import { useRef, type PointerEvent } from "react";
import { useTranslation } from "react-i18next";
import type { SegmentDto } from "../../lib/types";
import { flowSpans, seekMsFromFraction, speakerShares } from "../../lib/conversationFlow";
import { speakerColorMap } from "../../lib/speakerColors";
import { PauseIcon, PlayIcon } from "./SectionIcons";

/// The transcript's embedded player. Instead of a plain progress bar it shows the **conversation flow**:
/// the recording laid out left to right, each speaker's turns as coloured blocks proportional to their
/// span, silence left dark. That makes the shape of the meeting legible at a glance — who dominated,
/// where the long monologues are — and it doubles as the scrub target.
///
/// The layout and seek maths live in `lib/conversationFlow.ts`; this component only turns spans into
/// widths and pointer positions into a fraction.

function fmt(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
}

export default function ConversationFlowPlayer({
  segments,
  durationMs,
  currentMs,
  playing,
  speakerNameOf,
  showOriginal,
  canToggleOriginal,
  onToggle,
  onSeek,
  onToggleOriginal,
}: {
  segments: SegmentDto[];
  durationMs: number;
  currentMs: number;
  playing: boolean;
  speakerNameOf: (label: string) => string;
  showOriginal: boolean;
  /// Only offered when some segment actually has a revision to compare against.
  canToggleOriginal: boolean;
  onToggle: () => void;
  onSeek: (ms: number) => void;
  onToggleOriginal: () => void;
}) {
  const { t } = useTranslation("workspace");
  const trackRef = useRef<HTMLDivElement>(null);

  const labels = segments.map((s) => s.speaker);
  const colors = speakerColorMap(labels);
  const spans = flowSpans(segments, durationMs);
  const shares = speakerShares(segments);
  const playheadPct = durationMs > 0 ? Math.min(100, (currentMs / durationMs) * 100) : 0;

  // Click or drag anywhere on the track to seek. Pointer capture keeps the drag alive when the cursor
  // leaves the element; `seekMsFromFraction` clamps, so running past either end pins to start/end.
  const seekTo = (e: PointerEvent<HTMLDivElement>) => {
    const el = trackRef.current;
    if (!el) return;
    const { left, width } = el.getBoundingClientRect();
    if (width <= 0) return;
    onSeek(seekMsFromFraction((e.clientX - left) / width, durationMs));
  };

  const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
    // Pointer capture is what keeps a drag alive once the cursor leaves the track. It is not essential —
    // and jsdom doesn't implement it — so a click still seeks if it's unavailable.
    e.currentTarget.setPointerCapture?.(e.pointerId);
    seekTo(e);
  };
  const onPointerMove = (e: PointerEvent<HTMLDivElement>) => {
    if (e.currentTarget.hasPointerCapture?.(e.pointerId)) seekTo(e);
  };

  return (
    <div className="rounded-xl border border-gray-200 bg-white p-3.5 dark:border-gray-700 dark:bg-gray-800">
      <div className="flex items-center gap-3.5">
        <button
          type="button"
          onClick={onToggle}
          aria-label={playing ? t("pauseAudio") : t("playAll")}
          className="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-blue-600 text-white hover:bg-blue-700"
        >
          {playing ? <PauseIcon size={19} /> : <PlayIcon size={19} />}
        </button>

        <span className="shrink-0 text-xs font-semibold tabular-nums text-gray-900 dark:text-gray-100">
          {fmt(currentMs)}
        </span>

        <div
          ref={trackRef}
          role="slider"
          tabIndex={0}
          aria-label={t("seek")}
          aria-valuemin={0}
          aria-valuemax={Math.round(durationMs)}
          aria-valuenow={Math.round(currentMs)}
          aria-valuetext={fmt(currentMs)}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onKeyDown={(e) => {
            if (e.key === "ArrowRight") onSeek(Math.min(durationMs, currentMs + 5000));
            else if (e.key === "ArrowLeft") onSeek(Math.max(0, currentMs - 5000));
            else return;
            e.preventDefault();
          }}
          className="relative h-5 min-w-0 flex-1 cursor-pointer overflow-hidden rounded-md bg-gray-100 dark:bg-gray-900"
        >
          <div className="flex h-full w-full">
            {spans.map((span, i) => (
              <span
                key={i}
                style={{
                  width: `${span.widthPct}%`,
                  backgroundColor: span.label ? colors.get(span.label) : undefined,
                }}
                className={span.label ? "" : "bg-gray-200 dark:bg-gray-900"}
              />
            ))}
          </div>
          {/* Playhead: a white line with a dot, riding above the spans. */}
          <span
            aria-hidden
            style={{ left: `${playheadPct}%` }}
            className="pointer-events-none absolute -top-0.5 -bottom-0.5 w-0.5 -translate-x-1/2 bg-white shadow-[0_0_0_1px_rgba(0,0,0,.4)]"
          />
          <span
            aria-hidden
            style={{ left: `${playheadPct}%` }}
            className="pointer-events-none absolute top-0 h-2 w-2 -translate-x-1/2 rounded-full bg-white ring-2 ring-blue-600"
          />
        </div>

        <span className="shrink-0 text-xs tabular-nums text-gray-500 dark:text-gray-400">{fmt(durationMs)}</span>

        {canToggleOriginal && (
          <>
            <span className="h-5 w-px shrink-0 bg-gray-200 dark:bg-gray-600" aria-hidden />
            <button
              type="button"
              onClick={onToggleOriginal}
              aria-pressed={showOriginal}
              className="shrink-0 rounded-lg border border-gray-200 px-3 py-1.5 text-xs text-gray-600 hover:bg-gray-50 dark:border-gray-600 dark:text-gray-300 dark:hover:bg-gray-700"
            >
              {showOriginal ? t("showRevised") : t("showOriginal")}
            </button>
          </>
        )}
      </div>

      {shares.length > 0 && (
        <div className="mt-2.5 flex flex-wrap gap-3 text-[11px] text-gray-500 dark:text-gray-400">
          {shares.map((s) => (
            <span key={s.label} className="inline-flex items-center gap-1.5">
              <span
                aria-hidden
                style={{ backgroundColor: colors.get(s.label) }}
                className="h-2.5 w-2.5 rounded-sm"
              />
              {speakerNameOf(s.label)} {Math.round(s.pct)}%
            </span>
          ))}
        </div>
      )}
    </div>
  );
}
