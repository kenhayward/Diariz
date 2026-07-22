import { useTranslation } from "react-i18next";
import ToolbarButton, { iconProps } from "./ToolbarButton";
import FlaskIcon from "./FlaskIcon";

/// Toolbar for the Formulas tab: Run formula (always enabled) · Open / Download / Email (read - gated only on
/// a result being selected) · Delete (a mutation - additionally gated on `canManageSelected`, since the server
/// only lets the result's creator or someone with room-manage permission delete it: mirrors
/// `SectionFormulaResultsController.CanEditAsync` / `FormulaResultsController.CanEdit`). Modeled on
/// ActionsToolbar/the transcript segment toolbar. Selection is a single id lifted to RecordingDetail/
/// SectionDetail and shared with FormulasManager.
export default function FormulasToolbar({
  selectedId,
  canManageSelected,
  onRun,
  onOpen,
  onDownload,
  onEmail,
  onDelete,
}: {
  selectedId: string | null;
  /// Whether the CALLER may mutate the currently selected result (creator, or - per side - room ManageContents
  /// / recording ownership). Ignored when nothing is selected. Only gates Delete: Open/Download/Email are
  /// reads, available to anyone who can already see the result.
  canManageSelected: boolean;
  onRun: () => void;
  onOpen: () => void;
  onDownload: () => void;
  onEmail: () => void;
  onDelete: () => void;
}) {
  const { t } = useTranslation("workspace");
  const hasSelection = selectedId != null;

  // No own flex wrapper: DetailTabs already renders the active tab's toolbar inside one (see
  // RecordingDetail's "overview"/"actions" tabs, which inline bare ToolbarButtons the same way).
  return (
    <>
      <ToolbarButton label={t("runFormula")} icon={<FlaskIcon />} onClick={onRun} />
      <ToolbarButton label={t("openFormula")} icon={<OpenIcon />} onClick={onOpen} disabled={!hasSelection} />
      <ToolbarButton label={t("downloadFormula")} icon={<DownloadIcon />} onClick={onDownload} disabled={!hasSelection} />
      <ToolbarButton label={t("emailFormula")} icon={<MailIcon />} onClick={onEmail} disabled={!hasSelection} />
      <ToolbarButton label={t("deleteFormula")} icon={<TrashIcon />} onClick={onDelete} disabled={!hasSelection || !canManageSelected} />
      {hasSelection && <span className="ml-1 text-xs text-blue-700 dark:text-blue-300">1</span>}
    </>
  );
}

const OpenIcon = () => (
  <svg {...iconProps}>
    <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
    <polyline points="15 3 21 3 21 9" />
    <line x1="10" y1="14" x2="21" y2="3" />
  </svg>
);
const DownloadIcon = () => (
  <svg {...iconProps}>
    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
    <polyline points="7 10 12 15 17 10" />
    <line x1="12" y1="15" x2="12" y2="3" />
  </svg>
);
const MailIcon = () => (
  <svg {...iconProps}>
    <path d="M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z" />
    <polyline points="22,6 12,13 2,6" />
  </svg>
);
const TrashIcon = () => (
  <svg {...iconProps}>
    <polyline points="3 6 5 6 21 6" />
    <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
  </svg>
);
