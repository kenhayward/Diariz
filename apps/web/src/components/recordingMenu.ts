import type { TFunction } from "i18next";
import type { KebabAction } from "./KebabMenu";

export interface RecordingMenuHandlers {
  onRename: () => void;
  onRetranscribe: () => void;
  onSummarise: () => void;
  /// Copy a persistent rich-text link to this transcript (name as link text). Both surfaces.
  onCopyLink: () => void;
  /// Optional (detail page only): manually edit the transcript's summary.
  onEditSummary?: () => void;
  onMove: () => void;
  /// Optional (detail page only): extract action items from the transcript with the LLM.
  onExtractActions?: () => void;
  /// Optional (detail page only): re-run speaker identification against current voiceprints.
  onReidentify?: () => void;
  /// Optional (detail page only): translate the transcript into the user's native language.
  onTranslate?: () => void;
  /// The native-language label shown on the Translate item, e.g. "Spanish". Required for the item to appear.
  translateLabel?: string;
  /// Optional: the list omits Play (playback lives in the detail panel); the detail menu provides it.
  onPlay?: () => void;
  /// Opens the "Download as…" format chooser (Plain Text / Markdown / RTF).
  onDownloadTranscript: () => void;
  onDownloadAudio: () => void;
  /// Delete just the audio (keeps the transcript). Shown only while the recording still has audio.
  onDeleteAudio: () => void;
  /// Optional (detail page only): email the transcript to the signed-in user.
  onEmailTranscript?: () => void;
  onDelete: () => void;
  /// Transcript-dependent actions are disabled until a transcript exists.
  hasTranscript: boolean;
  /// Whether the audio is still present. Audio-dependent actions (re-transcribe, re-identify, play,
  /// download/delete audio) are hidden once the audio has been deleted.
  hasAudio: boolean;
  isSummarizing?: boolean;
}

/// The single source of truth for a recording's kebab actions, so the list and detail menus
/// always show the same items in the same order. Labels are localized via `t` at the call site
/// (the `recordings` namespace).
export function recordingMenu(h: RecordingMenuHandlers, t: TFunction): KebabAction[] {
  return [
    { label: t("recordings:rename"), onClick: h.onRename },
    { label: t("recordings:copyLink"), onClick: h.onCopyLink },
    // Re-transcribe needs the audio, so it's hidden once the audio is deleted.
    ...(h.hasAudio ? [{ label: t("recordings:retranscribe"), onClick: h.onRetranscribe }] : []),
    { label: t("recordings:summarise"), onClick: h.onSummarise, disabled: !h.hasTranscript || h.isSummarizing },
    ...(h.onEditSummary
      ? [{ label: t("recordings:editSummary"), onClick: h.onEditSummary, disabled: !h.hasTranscript }]
      : []),
    ...(h.onExtractActions
      ? [{ label: t("recordings:extractActions"), onClick: h.onExtractActions, disabled: !h.hasTranscript }]
      : []),
    ...(h.onReidentify && h.hasAudio
      ? [{ label: t("recordings:reidentifySpeakers"), onClick: h.onReidentify, disabled: !h.hasTranscript }]
      : []),
    ...(h.onTranslate && h.translateLabel
      ? [{ label: t("recordings:translateTo", { language: h.translateLabel }), onClick: h.onTranslate, disabled: !h.hasTranscript }]
      : []),
    { label: t("recordings:moveToSection"), onClick: h.onMove },
    ...(h.onPlay && h.hasAudio ? [{ label: t("recordings:play"), onClick: h.onPlay }] : []),
    { label: t("recordings:downloadTranscript"), onClick: h.onDownloadTranscript, disabled: !h.hasTranscript },
    ...(h.onEmailTranscript
      ? [{ label: t("recordings:emailTranscript"), onClick: h.onEmailTranscript, disabled: !h.hasTranscript }]
      : []),
    ...(h.hasAudio ? [{ label: t("recordings:downloadAudio"), onClick: h.onDownloadAudio }] : []),
    ...(h.hasAudio ? [{ label: t("recordings:deleteAudio"), danger: true, onClick: h.onDeleteAudio }] : []),
    { label: t("recordings:delete"), danger: true, onClick: h.onDelete },
  ];
}
