import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { useSelection } from "../lib/selection";
import { completeTarget } from "../lib/actionsView";
import ToolbarButton, { iconProps } from "./ToolbarButton";
import type { ActionListItem } from "../lib/types";

/// Toolbar for the Actions tab: Refresh · Select (toggle, reused from the list view) · Mark Complete
/// (≥1 selected; toggles via completeTarget) · Edit (exactly 1 selected) · Hide Complete (toggle).
export default function ActionsToolbar({
  actions,
  hideComplete,
  onToggleHideComplete,
  onEdit,
  onError,
}: {
  /// The full (unfiltered) action list, so a selected id can be resolved to its completion state.
  actions: ActionListItem[];
  hideComplete: boolean;
  onToggleHideComplete: () => void;
  onEdit: () => void;
  onError: (msg: string | null) => void;
}) {
  const { t } = useTranslation("workspace");
  const qc = useQueryClient();
  const { selectMode, setSelectMode, selectedIds, clear } = useSelection();

  const selected = actions.filter((a) => selectedIds.includes(a.id));

  async function markComplete() {
    if (selected.length === 0) return;
    const target = completeTarget(selected);
    onError(null);
    try {
      await api.completeActions(selected.map((a) => a.id), target);
      clear();
      qc.invalidateQueries({ queryKey: ["actions", "all"] });
      qc.invalidateQueries({ queryKey: ["recording"] }); // any open transcript reflects the new state
    } catch (e) {
      onError(apiErrorMessage(e));
    }
  }

  return (
    <div className="flex h-9 items-center gap-0.5 border-b px-3 dark:border-gray-700">
      <ToolbarButton
        label={t("refresh")}
        onClick={() => qc.invalidateQueries({ queryKey: ["actions", "all"] })}
        icon={<RefreshIcon />}
      />
      <ToolbarButton
        label={selectMode ? t("doneSelecting") : t("selectActions")}
        onClick={() => {
          if (selectMode) clear(); // leaving Select mode deselects everything (clears the count badge)
          setSelectMode(!selectMode);
        }}
        active={selectMode}
        icon={<SelectIcon />}
      />
      <ToolbarButton
        label={completeTarget(selected) ? t("markComplete") : t("markIncomplete")}
        onClick={markComplete}
        disabled={selected.length === 0}
        icon={<CheckCircleIcon />}
      />
      <ToolbarButton
        label={t("editAction")}
        onClick={onEdit}
        disabled={selectedIds.length !== 1}
        icon={<PencilIcon />}
      />
      <ToolbarButton
        label={hideComplete ? t("showComplete") : t("hideComplete")}
        onClick={onToggleHideComplete}
        active={hideComplete}
        icon={<EyeOffIcon />}
      />
      {selectedIds.length > 0 && (
        <span className="ml-1 text-xs text-blue-700 dark:text-blue-300">{selectedIds.length}</span>
      )}
    </div>
  );
}

const RefreshIcon = () => (
  <svg {...iconProps}>
    <path d="M23 4v6h-6" />
    <path d="M1 20v-6h6" />
    <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15" />
  </svg>
);
const SelectIcon = () => (
  <svg {...iconProps}>
    <path d="M9 11l3 3L22 4" />
    <path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" />
  </svg>
);
const CheckCircleIcon = () => (
  <svg {...iconProps}>
    <path d="M22 11.08V12a10 10 0 1 1-5.93-9.14" />
    <polyline points="22 4 12 14.01 9 11.01" />
  </svg>
);
const PencilIcon = () => (
  <svg {...iconProps}>
    <path d="M12 20h9" />
    <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4Z" />
  </svg>
);
const EyeOffIcon = () => (
  <svg {...iconProps}>
    <path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24" />
    <line x1="1" y1="1" x2="23" y2="23" />
  </svg>
);
