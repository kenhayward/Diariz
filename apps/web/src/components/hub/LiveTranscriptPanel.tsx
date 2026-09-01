import { useTranslation } from "react-i18next";
import type { LiveTranscript } from "../../lib/liveTranscript";

export type LiveTranscriptPanelProps = {
  transcript: LiveTranscript;
  /// How far behind the meeting the text is, in whole seconds.
  lagSeconds: number;
  /// The server has stopped transcribing live for this recording - too far behind, or a chunk failed.
  /// Capture is unaffected either way.
  degraded: boolean;
};

/// The transcript of the meeting you are currently in.
///
/// One thing it deliberately does not do.
///
/// It never presents itself as finished. The text is provisional - the final pass over the whole
/// recording replaces it when the meeting ends - so the status line says so rather than leaving someone
/// to copy a half-written transcript out as the record.
export default function LiveTranscriptPanel({
  transcript,
  lagSeconds,
  degraded,
}: LiveTranscriptPanelProps) {
  const { t } = useTranslation("recordings");

  const status = degraded
    ? t("liveTranscriptDegraded")
    : lagSeconds > 0
      ? t("liveTranscriptBehind", { seconds: lagSeconds })
      : t("liveTranscriptLive");

  return (
    <div className="flex flex-col gap-2">
      <p
        data-testid="live-transcript-status"
        className="text-xs text-[var(--muted)]"
        // Not role="alert": falling behind is not an error, and announcing it as one would interrupt
        // whatever the user is doing to tell them about something that fixes itself.
        aria-live="polite"
      >
        {status}
      </p>

      {transcript.segments.length === 0 ? (
        <p data-testid="live-transcript-empty" className="text-sm text-[var(--muted)]">
          {t("liveTranscriptEmpty")}
        </p>
      ) : (
        <ol className="flex list-none flex-col gap-1 p-0">
          {transcript.segments.map((s, i) => {
            // The name marks where the speaker changes rather than repeating down a long turn, which
            // would be noise: the transcript reads as a conversation.
            const changed = s.speaker && s.speaker !== transcript.segments[i - 1]?.speaker;
            return (
              <li key={s.id} data-testid="live-transcript-line" className="text-sm leading-relaxed">
                {changed && (
                  <span
                    data-testid="live-transcript-speaker"
                    // A suggestion is the server asking, not answering. Rendering it identically to a
                    // confident match would give a coin flip the authority of a confirmed name.
                    data-suggestion={s.speakerIsSuggestion ? "true" : undefined}
                    className={
                      s.speakerIsSuggestion
                        ? "mr-2 text-xs italic text-[var(--muted)]"
                        : "mr-2 text-xs font-medium text-[var(--muted)]"
                    }
                    title={s.speakerIsSuggestion ? t("liveTranscriptSpeakerGuess") : undefined}
                  >
                    {s.speaker}
                    {s.speakerIsSuggestion ? "?" : ""}
                  </span>
                )}
                {s.text}
              </li>
            );
          })}
        </ol>
      )}
    </div>
  );
}
