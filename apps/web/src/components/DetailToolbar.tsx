import { useTranslation } from "react-i18next";
import ToolbarButton, { iconProps } from "./ToolbarButton";

/// A row of graphical shortcut buttons for the most common recording actions, shown on the detail
/// header just left of the kebab menu. Every action here is also available in the kebab (see
/// `recordingMenu`) — this is a quality-of-life surface, not the source of truth.
export default function DetailToolbar({
  onRename,
  onCopyLink,
  onRetranscribe,
  onMove,
  onEmailTranscript,
  onDownloadTranscript,
  hasTranscript,
  hasAudio,
}: {
  onRename: () => void;
  onCopyLink: () => void;
  onRetranscribe: () => void;
  onMove: () => void;
  onEmailTranscript: () => void;
  onDownloadTranscript: () => void;
  hasTranscript: boolean;
  /// Re-transcribe needs the audio, so its button is hidden once the audio has been deleted.
  hasAudio: boolean;
}) {
  const { t } = useTranslation("recordings");
  return (
    <div className="flex items-center gap-0.5">
      <ToolbarButton label={t("rename")} onClick={onRename} icon={<EditIcon />} />
      <ToolbarButton label={t("copyLink")} onClick={onCopyLink} icon={<LinkIcon />} />
      {hasAudio && <ToolbarButton label={t("retranscribe")} onClick={onRetranscribe} icon={<RefreshIcon />} />}
      <ToolbarButton label={t("moveToSectionShort")} onClick={onMove} icon={<FolderIcon />} />
      <ToolbarButton
        label={t("emailTranscript")}
        onClick={onEmailTranscript}
        icon={<MailIcon />}
        disabled={!hasTranscript}
      />
      <ToolbarButton
        label={t("downloadTranscript")}
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
const LinkIcon = () => (
  <svg {...iconProps}>
    <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
    <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
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
