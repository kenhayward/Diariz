import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import type { SegmentDto, SpeakerInfo } from "../../lib/types";

/// Divides one segment in two at a word boundary, and hands the new half to whoever actually said it.
///
/// This exists for the block that is *mostly* one person with someone else's few words inside it. Naming
/// the dominant speaker used to enrol the interloper's audio along with theirs, because a voiceprint is
/// built from whatever the segment covers.
///
/// **The cut points are the gaps between words, never the ends.** A gap before the first word or after the
/// last would leave a half empty, which the server rejects - offering it would be a control that only ever
/// produces an error.
///
/// Its own component rather than more of `RecordingDetail` (already 1800+ lines): it owns a fetch, a
/// confirm, and a two-step flow, none of which the page has any other reason to know about.
export default function SegmentSplitModal({
  recordingId,
  seg,
  speakers,
  onClose,
  onDone,
}: {
  recordingId: string;
  seg: SegmentDto;
  /// The recording's existing speakers, offered as destinations for the new half.
  speakers: SpeakerInfo[];
  onClose: () => void;
  /// Fired after the split (and any reassignment) succeeded, so the page can refetch.
  onDone: () => void;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  const [cutAt, setCutAt] = useState<number | null>(null);
  const [newSpeaker, setNewSpeaker] = useState<string>(seg.speaker);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Fetched per segment, not carried on the transcript: a long meeting has ~10k words.
  const { data: words = [], isLoading } = useQuery({
    queryKey: ["segmentWords", recordingId, seg.id],
    queryFn: () => api.getSegmentWords(recordingId, seg.id),
  });

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function split() {
    if (cutAt == null) return;

    // The API refuses an unconfirmed split of an edited segment rather than dropping the edit silently,
    // so ask here and say exactly what goes.
    if (seg.revised != null && !window.confirm(t("workspace:confirmSplitDiscardsEdit"))) return;

    setBusy(true);
    setError(null);
    try {
      await api.splitSegment(recordingId, seg.id, cutAt, seg.revised != null);

      if (newSpeaker !== seg.speaker) {
        // The right-hand half is the piece that was cut off, so that is the one to move. Its id only
        // exists after the split, hence the refetch.
        const rec = await api.getRecording(recordingId);
        const right = rec.current?.segments.find((s) => s.startMs === words[cutAt].s);
        if (right) await api.assignSegmentSpeaker(recordingId, right.id, newSpeaker || null);
      }

      onDone();
    } catch (e) {
      setError(apiErrorMessage(e, t("workspace:errSplitSegment")));
    } finally {
      setBusy(false);
    }
  }

  const left = cutAt == null ? [] : words.slice(0, cutAt);
  const right = cutAt == null ? [] : words.slice(cutAt);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("workspace:splitSegment")}
        className="w-full max-w-3xl rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="text-base font-semibold dark:text-gray-100">{t("workspace:splitSegment")}</h2>
        <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">{t("workspace:splitHint")}</p>

        {isLoading ? (
          <p className="mt-4 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>
        ) : (
          <div className="mt-4 flex flex-wrap items-center gap-y-1 text-sm dark:text-gray-200">
            {words.map((w, i) => (
              // The gap BEFORE word i is cut point i. Index 0 is skipped: it would leave nothing on the
              // left, which the server rejects.
              <span key={i} className="contents">
                {i > 0 && (
                  <button
                    type="button"
                    aria-label={t("workspace:splitHereBefore", { word: w.w })}
                    onClick={() => setCutAt(i)}
                    className={`mx-0.5 rounded px-1 text-xs leading-5 ${
                      cutAt === i
                        ? "bg-blue-600 text-white"
                        : "text-gray-300 hover:bg-blue-100 hover:text-blue-700 dark:text-gray-600 dark:hover:bg-blue-900 dark:hover:text-blue-200"
                    }`}
                  >
                    ✂
                  </button>
                )}
                <span>{w.w}</span>
              </span>
            ))}
          </div>
        )}

        {cutAt != null && (
          <div data-testid="split-preview" className="mt-4 space-y-2 rounded border p-3 text-sm dark:border-gray-700">
            <p className="dark:text-gray-200">
              <span className="mr-2 text-xs text-gray-500 dark:text-gray-400">{seg.speakerDisplay}</span>
              {left.map((w) => w.w).join(" ")}
            </p>
            <p className="dark:text-gray-200">
              <span className="mr-2 text-xs text-gray-500 dark:text-gray-400">
                {speakers.find((s) => s.label === newSpeaker)?.displayName ?? t("workspace:splitNewSpeaker")}
              </span>
              {right.map((w) => w.w).join(" ")}
            </p>
          </div>
        )}

        <label className="mt-4 block text-sm">
          <span className="mb-1 block text-gray-600 dark:text-gray-300">
            {t("workspace:splitNewPartSpeaker")}
          </span>
          <select
            value={newSpeaker}
            onChange={(e) => setNewSpeaker(e.target.value)}
            className="w-64 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          >
            {speakers.map((s) => (
              <option key={s.label} value={s.label}>
                {s.displayName}
              </option>
            ))}
            {/* Empty value = "mint one": the interrupting voice often has no diarization slot of its own,
                and the client must not invent a label into the worker's namespace. */}
            <option value="">{t("workspace:splitNewSpeaker")}</option>
          </select>
        </label>

        {seg.revised != null && (
          <p className="mt-3 text-xs text-amber-700 dark:text-amber-400">
            {t("workspace:splitDiscardsEditWarning")}
          </p>
        )}
        {error && <p className="mt-3 text-sm text-red-600 dark:text-red-400">{error}</p>}

        <div className="mt-4 flex items-center justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="button"
            onClick={split}
            disabled={busy || cutAt == null}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {t("workspace:splitConfirm")}
          </button>
        </div>
      </div>
    </div>
  );
}
