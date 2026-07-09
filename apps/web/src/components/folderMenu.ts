import type { TFunction } from "i18next";
import type { KebabAction } from "./KebabMenu";

export interface FolderMenuHandlers {
  onRename: () => void;
  onCopyLink: () => void;
}

/// The folder (section) page's kebab/toolbar actions. Deliberately minimal - a folder is an aggregate view,
/// so only Edit Name and Copy Link apply (its recordings are managed from the sidebar).
export function folderMenu(h: FolderMenuHandlers, t: TFunction): KebabAction[] {
  return [
    { label: t("recordings:rename"), onClick: h.onRename },
    { label: t("recordings:copyLink"), onClick: h.onCopyLink },
  ];
}
