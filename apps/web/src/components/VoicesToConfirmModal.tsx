import { useCallback, useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { formatDuration } from "../lib/format";
import { clipQueue } from "../lib/clipPlayback";
import { useClipPlayer } from "../lib/useClipPlayer";
import { CheckIcon, PlayIcon, StopIcon, XIcon } from "./icons";
import HelpButton from "./HelpButton";
import type { SpeakerSuggestion } from "../lib/types";

/// Voices Diariz thinks it recognises but would not name on its own, gathered in one place.
///
/// A modal rather than a route, like the People directory: the queue is worked through in a sitting, and a
/// route would throw away whatever the user was reading. Deliberately **not** a panel inside People - this
/// needs no permission, because a suggestion asks "is this speaker that person?" and the people who can
/// answer were in the meeting, whereas the directory itself is gated on Manage people.
///
/// Two panels: the queue on the left, and on the right the evidence for whichever voice is open - what they
/// said, with each segment playable. Listening is the only honest way to answer, which is why the queue only
/// ever offers voices whose audio still exists (see SpeakerSuggestionsController.Pending).
///
/// The same accept and reject as the prompt on the transcript, so a decision made here is the same decision
/// made there.
export default function VoicesToConfirmModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation(["workspace", "common"]);
  const [error, setError] = useState<string | null>(null);
  /// Ids already decided, so a voice leaves the queue immediately rather than after a refetch.
  const [done, setDone] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState<string | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
    }
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const { data = [], isLoading, isError } = useQuery({
    queryKey: ["speakerSuggestions"],
    queryFn: () => api.getSpeakerSuggestions(),
  });

  const rows = data.filter((s) => !done.has(s.speakerId));
  /// Derived rather than stored, so one rule covers three cases: nothing chosen yet, the open voice just
  /// decided, and a refetch that dropped it. All three land on the top of the queue, which is the closest
  /// call and the one most likely to be right.
  const open = rows.find((s) => s.speakerId === openId) ?? rows[0] ?? null;
  const openSpeakerId = open?.speakerId ?? null;

  const fetchClip = useCallback(
    (speakerId: string, fromMs: number, toMs: number) => api.suggestionClip(speakerId, fromMs, toMs),
    [],
  );
  const { play, stop, playingSegmentId } = useClipPlayer(fetchClip);

  // The segment list is replaced when the open voice changes, so without this a clip would play on with
  // nothing on screen able to stop it.
  useEffect(() => stop(), [openSpeakerId, stop]);

  const { data: segments = [], isLoading: segmentsLoading } = useQuery({
    queryKey: ["suggestion-segments", openSpeakerId],
    queryFn: () => api.getSuggestionSegments(openSpeakerId!),
    enabled: openSpeakerId !== null,
  });

  async function decide(s: SpeakerSuggestion, accept: boolean) {
    setBusy(s.speakerId);
    setError(null);
    try {
      if (accept) await api.acceptSpeakerSuggestion(s.speakerId);
      else await api.rejectSpeakerSuggestion(s.speakerId);
      // Only after the server agreed: dropping the voice on a failure would look like the decision stuck,
      // and it would return on the next load with nothing to explain why.
      setDone((prev) => new Set(prev).add(s.speakerId));
      setOpenId(null);
    } catch (e) {
      setError(apiErrorMessage(e, t("workspace:suggestionFailed")));
    } finally {
      setBusy(null);
    }
  }

  /// Tick and cross rather than words: the pair sits inline at text height, so the decision is one glance
  /// and one click away from the evidence it is about.
  const verdictButton = (accept: boolean) => (
    <button
      type="button"
      onClick={() => open && void decide(open, accept)}
      disabled={open === null || busy === openSpeakerId}
      aria-label={accept ? t("workspace:suggestionYes") : t("workspace:suggestionNo")}
      title={accept ? t("workspace:suggestionYes") : t("workspace:suggestionNo")}
      className={`inline-flex h-7 w-7 shrink-0 items-center justify-center rounded border disabled:opacity-50 ${
        accept
          ? "border-green-600 text-green-700 hover:bg-green-50 dark:border-green-500 dark:text-green-400 dark:hover:bg-green-900/30"
          : "border-red-600 text-red-700 hover:bg-red-50 dark:border-red-500 dark:text-red-400 dark:hover:bg-red-900/30"
      }`}
    >
      {accept ? <CheckIcon size={16} /> : <XIcon size={16} />}
    </button>
  );

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-label={t("workspace:suggestionsTitle")}
        className="flex h-[88vh] w-[92vw] max-w-6xl flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-center justify-between border-b px-5 py-3 dark:border-gray-700">
          <h2 className="flex items-center gap-1.5 text-base font-semibold dark:text-gray-100">
            {t("workspace:suggestionsTitle")}
            <HelpButton topic="transcription-and-speakers" />
          </h2>
          <button
            type="button"
            aria-label={t("common:close")}
            onClick={onClose}
            className="rounded p-1 text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
          >
            ✕
          </button>
        </div>

        <div className="flex min-h-0 flex-1 flex-col gap-3 p-4">
          <p className="shrink-0 text-sm text-gray-600 dark:text-gray-300">
            {t("workspace:suggestionsIntro")}
          </p>

          {error && <p className="shrink-0 text-sm text-red-600 dark:text-red-400">{error}</p>}

          {isLoading ? (
            <p className="text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>
          ) : isError ? (
            <p className="text-sm text-red-600 dark:text-red-400">
              {t("workspace:suggestionsFailed")}
            </p>
          ) : rows.length === 0 ? (
            <p className="text-sm text-gray-500 dark:text-gray-400">{t("workspace:suggestionsNone")}</p>
          ) : (
            <div className="flex min-h-0 flex-1 gap-4">
              {/* The queue. One of the two scrolling regions. */}
              <ul className="w-80 shrink-0 overflow-y-auto rounded border dark:border-gray-700">
                {rows.map((s) => (
                  <li key={s.speakerId}>
                    <button
                      type="button"
                      onClick={() => setOpenId(s.speakerId)}
                      aria-pressed={s.speakerId === openSpeakerId}
                      className={`block w-full px-3 py-1.5 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800 ${
                        s.speakerId === openSpeakerId ? "bg-gray-100 dark:bg-gray-800" : ""
                      }`}
                    >
                      {/* truncate needs a block: on an inline span it sets only white-space:nowrap, so the
                          text would neither wrap nor ellipsise and would overflow the row. */}
                      <span className="block truncate text-gray-800 dark:text-gray-100">
                        {s.personName}
                      </span>
                      <span className="block truncate text-xs text-gray-500 dark:text-gray-400">
                        {s.recordingName} - {s.speakerLabel} - {formatDuration(s.speechMs)}
                      </span>
                    </button>
                  </li>
                ))}
              </ul>

              {/* The evidence for whichever voice is open. */}
              <section
                aria-label={t("workspace:suggestionEvidence")}
                className="flex min-h-0 flex-1 flex-col rounded border dark:border-gray-700"
              >
                <div className="flex shrink-0 flex-wrap items-center gap-2 border-b px-3 py-2 dark:border-gray-700">
                  <span className="min-w-0 flex-1 truncate text-sm text-gray-800 dark:text-gray-100">
                    {open && t("workspace:suggestionAsk", { name: open.personName })}
                  </span>
                  {verdictButton(true)}
                  {verdictButton(false)}
                </div>

                <ul className="min-h-0 flex-1 overflow-y-auto">
                  {segmentsLoading ? (
                    <li className="p-3 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</li>
                  ) : segments.length === 0 ? (
                    <li className="p-3 text-sm text-gray-500 dark:text-gray-400">
                      {t("workspace:suggestionNoWords")}
                    </li>
                  ) : (
                    segments.map((g) => {
                      const playing = playingSegmentId === g.id;
                      return (
                        // One line per segment: the control, the time, the words. Every part is text
                        // height, so a long list stays a single line a row and scannable.
                        <li
                          key={g.id}
                          className="flex items-center gap-2 px-3 py-1 text-sm hover:bg-gray-50 dark:hover:bg-gray-800"
                        >
                          {/* One button, two states. A separate stop control would sit dead on every row
                              that is not playing. */}
                          <button
                            type="button"
                            onClick={() =>
                              playing
                                ? stop()
                                : void play(openSpeakerId!, clipQueue(segments, [g.id]))
                            }
                            aria-label={
                              playing ? t("workspace:suggestionStop") : t("workspace:suggestionPlay")
                            }
                            title={playing ? t("workspace:suggestionStop") : t("workspace:suggestionPlay")}
                            // h-5 matches the 20px line box of the text beside it, measured in the browser, so an ordinary
                            // segment is one line high rather than being padded out by its own control.
                            className="inline-flex h-5 w-5 shrink-0 items-center justify-center rounded border text-gray-600 hover:bg-gray-100 dark:border-gray-600 dark:text-gray-300 dark:hover:bg-gray-700"
                          >
                            {playing ? <StopIcon size={11} /> : <PlayIcon size={11} />}
                          </button>
                          {/* Where in the recording, not how long the segment is: it places what you are
                              hearing, and is what you would scrub to if you opened the transcript. */}
                          <span className="shrink-0 font-mono text-xs text-gray-500 dark:text-gray-400">
                            {formatDuration(g.startMs)}
                          </span>
                          <span className="min-w-0 flex-1 text-gray-700 dark:text-gray-200">{g.text}</span>
                        </li>
                      );
                    })
                  )}
                </ul>
              </section>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
