import type { KebabAction } from "./KebabMenu";

export interface RecordingMenuHandlers {
  onRename: () => void;
  onRetranscribe: () => void;
  onSummarise: () => void;
  onMove: () => void;
  onPlay: () => void;
  onDownloadTxt: () => void;
  onDownloadSrt: () => void;
  onDownloadAudio: () => void;
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
    { label: "Move to section…", onClick: h.onMove },
    { label: "Play", onClick: h.onPlay },
    { label: "Download transcript (.txt)", onClick: h.onDownloadTxt, disabled: !h.hasTranscript },
    { label: "Download transcript (.srt)", onClick: h.onDownloadSrt, disabled: !h.hasTranscript },
    { label: "Download audio", onClick: h.onDownloadAudio },
    { label: "Delete", danger: true, onClick: h.onDelete },
  ];
}
