import ToolbarButton, { iconProps } from "./ToolbarButton";

/// A row of graphical shortcut buttons for the most common recording actions, shown on the detail
/// header just left of the kebab menu. Every action here is also available in the kebab (see
/// `recordingMenu`) — this is a quality-of-life surface, not the source of truth.
export default function DetailToolbar({
  onRename,
  onRetranscribe,
  onMove,
  onExtractActions,
  onEmailTranscript,
  onDownloadTranscript,
  hasTranscript,
}: {
  onRename: () => void;
  onRetranscribe: () => void;
  onMove: () => void;
  onExtractActions: () => void;
  onEmailTranscript: () => void;
  onDownloadTranscript: () => void;
  hasTranscript: boolean;
}) {
  return (
    <div className="flex items-center gap-0.5">
      <ToolbarButton label="Rename" onClick={onRename} icon={<EditIcon />} />
      <ToolbarButton label="Re-transcribe" onClick={onRetranscribe} icon={<RefreshIcon />} />
      <ToolbarButton label="Move to section" onClick={onMove} icon={<FolderIcon />} />
      <ToolbarButton
        label="Extract actions"
        onClick={onExtractActions}
        icon={<ChecklistIcon />}
        disabled={!hasTranscript}
      />
      <ToolbarButton
        label="Email me the transcript"
        onClick={onEmailTranscript}
        icon={<MailIcon />}
        disabled={!hasTranscript}
      />
      <ToolbarButton
        label="Download transcript"
        onClick={onDownloadTranscript}
        icon={<DownloadIcon />}
        disabled={!hasTranscript}
      />
    </div>
  );
}

const EditIcon = () => (
  <svg {...iconProps}>
    <path d="M12 20h9" />
    <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4 12.5-12.5z" />
  </svg>
);
const RefreshIcon = () => (
  <svg {...iconProps}>
    <path d="M23 4v6h-6" />
    <path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" />
  </svg>
);
const FolderIcon = () => (
  <svg {...iconProps}>
    <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
  </svg>
);
const ChecklistIcon = () => (
  <svg {...iconProps}>
    <path d="M9 11l3 3L22 4" />
    <path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" />
  </svg>
);
const MailIcon = () => (
  <svg {...iconProps}>
    <path d="M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2z" />
    <polyline points="22,6 12,13 2,6" />
  </svg>
);
const DownloadIcon = () => (
  <svg {...iconProps}>
    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
    <polyline points="7 10 12 15 17 10" />
    <line x1="12" y1="15" x2="12" y2="3" />
  </svg>
);
