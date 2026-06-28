import type { KebabAction } from "./KebabMenu";

export interface RecordingMenuHandlers {
  onRename: () => void;
  onRetranscribe: () => void;
  onSummarise: () => void;
  onMove: () => void;
  /// Optional (detail page only): re-run speaker identification against current voiceprints.
  onReidentify?: () => void;
  /// Optional: the list omits Play (playback lives in the detail panel); the detail menu provides it.
  onPlay?: () => void;
  /// Opens the "Download as…" format chooser (Plain Text / Markdown / RTF).
  onDownloadTranscript: () => void;
  onDownloadAudio: () => void;
  /// Optional (detail page only): email the transcript to the signed-in user.
  onEmailTranscript?: () => void;
  onDelete: () => void;
  /// Transcript-dependent actions are disabled until a transcript exists.
  hasTranscript: boolean;
  isSummarizing?: boolean;
}

/// The single source of truth for a recording's kebab actions, so the list and detail menus
/// always show the same items in the same order.
export function recordingMenu(h: RecordingMenuHandlers): KebabAction[] {
  return [
    { label: "Rename", onClick: h.onRename },
    { label: "Re-transcribe", onClick: h.onRetranscribe },
    { label: "Summarise", onClick: h.onSummarise, disabled: !h.hasTranscript || h.isSummarizing },
    ...(h.onReidentify
      ? [{ label: "Re-identify speakers", onClick: h.onReidentify, disabled: !h.hasTranscript }]
      : []),
    { label: "Move to section…", onClick: h.onMove },
    ...(h.onPlay ? [{ label: "Play", onClick: h.onPlay }] : []),
    { label: "Download transcript", onClick: h.onDownloadTranscript, disabled: !h.hasTranscript },
    ...(h.onEmailTranscript
      ? [{ label: "Email me the transcript", onClick: h.onEmailTranscript, disabled: !h.hasTranscript }]
      : []),
    { label: "Download audio", onClick: h.onDownloadAudio },
    { label: "Delete", danger: true, onClick: h.onDelete },
  ];
}
