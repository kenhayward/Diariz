import { useTranslation } from "react-i18next";
import KebabMenu, { type KebabAction } from "../KebabMenu";
import { DownloadIcon, LinkIcon, PencilIcon, PlayIcon, StopIcon } from "./SectionIcons";

/// The recording-detail header: the title, and a consolidated action cluster.
///
/// This replaces `DetailToolbar`'s six-button row (rename / copy link / retranscribe / move / email /
/// download), which crowded the header and duplicated the kebab. Only the three actions people reach for
/// constantly stay visible — Play, Copy link, Download — and everything else lives in More. Nothing is
/// lost: the kebab (`recordingMenu`) was already the documented source of truth for every action, and it
/// still carries all of them.

export default function DetailHeader({
  title,
  menu,
  hasAudio,
  hasTranscript,
  isPlaying,
  onPlay,
  onStop,
  onRename,
  onCopyLink,
  onDownload,
}: {
  title: string;
  menu: KebabAction[];
  hasAudio: boolean;
  hasTranscript: boolean;
  isPlaying: boolean;
  onPlay: () => void;
  onStop: () => void;
  onRename: () => void;
  onCopyLink: () => void;
  onDownload: () => void;
}) {
  const { t } = useTranslation(["workspace", "recordings"]);

  return (
    <div className="flex items-start justify-between gap-5">
      <h1 className="min-w-0 truncate text-[22px] font-semibold tracking-tight text-gray-900 dark:text-gray-100">
        {title}
      </h1>

      <div className="flex shrink-0 items-center gap-1.5">
        <button
          type="button"
          onClick={isPlaying ? onStop : onPlay}
          disabled={!hasAudio}
          className="inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-3.5 py-2 text-[13px] font-semibold text-white hover:bg-blue-700 disabled:pointer-events-none disabled:opacity-40"
        >
          {isPlaying ? <StopIcon /> : <PlayIcon />}
          {isPlaying ? t("workspace:stopPlayback") : t("workspace:playRecording")}
        </button>

        <IconButton label={t("recordings:rename")} onClick={onRename}>
          <PencilIcon />
        </IconButton>

        <IconButton label={t("recordings:copyLink")} onClick={onCopyLink}>
          <LinkIcon />
        </IconButton>

        <IconButton label={t("recordings:downloadTranscript")} onClick={onDownload} disabled={!hasTranscript}>
          <DownloadIcon />
        </IconButton>

        <KebabMenu actions={menu} label={t("workspace:hubMoreActions")} />
      </div>
    </div>
  );
}

/// A bordered square icon button, sized to sit level with the Play button.
function IconButton({
  label,
  onClick,
  disabled,
  children,
}: {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      onClick={onClick}
      disabled={disabled}
      className="grid h-9 w-9 place-items-center rounded-lg border border-gray-200 bg-white text-gray-500 hover:bg-gray-50 disabled:pointer-events-none disabled:opacity-40 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-400 dark:hover:bg-gray-700"
    >
      {children}
    </button>
  );
}
