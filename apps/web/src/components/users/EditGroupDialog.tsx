import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../../lib/api";
import type { Group } from "../../lib/types";
import IconColorPicker from "../IconColorPicker";

/// The fallback swatch colour for a group that has never been given one - which, until this dialog existed,
/// was every group. Neutral rather than a guessed hue, so an unstyled group looks deliberately unstyled.
export const DEFAULT_GROUP_COLOR = "#9ca3af";

/// Rename a group, describe it, and give it a colour.
///
/// `description`, `icon` and `color` have been on `Group` and round-tripping through `updateGroup` all along,
/// but nothing ever set them - so the list swatch and the description line the design asks for would have
/// been blank forever. This is the screen that fills them in.
///
/// Carries `data-nested-dialog` so the console's Escape handler defers to it while it is open.
export default function EditGroupDialog({ group, onClose }: { group: Group; onClose: () => void }) {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const [name, setName] = useState(group.name);
  const [description, setDescription] = useState(group.description ?? "");
  const [icon, setIcon] = useState<string | null>(group.icon);
  const [color, setColor] = useState(group.color ?? DEFAULT_GROUP_COLOR);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const save = useMutation({
    // `updateGroup` takes the whole shape, not a patch - `permissions` must be passed through untouched or
    // saving a name would silently strip the group's rights.
    mutationFn: () =>
      api.updateGroup(group.id, {
        name: name.trim(),
        description: description.trim() || null,
        icon,
        color,
        permissions: group.permissions,
      }),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ["groups"] });
      onClose();
    },
    onError: (e: unknown) => setError(apiErrorMessage(e)),
  });

  return (
    <div
      data-nested-dialog
      data-testid="edit-group-backdrop"
      className="fixed inset-0 z-[60] flex items-center justify-center bg-black/40 p-4"
    >
      <form
        role="dialog"
        aria-label={t("editGroupTitle")}
        onSubmit={(e) => {
          e.preventDefault();
          setError(null);
          if (name.trim()) save.mutate();
        }}
        className="flex w-full max-w-md flex-col gap-3 rounded-lg border border-gray-200 bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <h2 className="text-sm font-semibold dark:text-gray-100">{t("editGroupTitle")}</h2>
        {error && <p className="text-xs text-red-600 dark:text-red-400">{error}</p>}

        <label className="space-y-1 text-xs text-gray-600 dark:text-gray-300">
          <span>{t("groupNameField")}</span>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            aria-label={t("groupNameField")}
            className="w-full rounded border border-gray-300 px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
        </label>

        <label className="space-y-1 text-xs text-gray-600 dark:text-gray-300">
          <span>{t("groupDescription")}</span>
          <input
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            aria-label={t("groupDescription")}
            className="w-full rounded border border-gray-300 px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
        </label>

        <div className="space-y-1 text-xs text-gray-600 dark:text-gray-300">
          <span>{t("groupAppearance")}</span>
          <IconColorPicker
            icon={icon}
            color={color}
            onChange={(patch) => {
              if (patch.icon !== undefined) setIcon(patch.icon);
              if (patch.color !== undefined) setColor(patch.color);
            }}
            colorLabel={t("groupAppearance")}
          />
        </div>

        <div className="flex justify-end gap-2 pt-1">
          <button
            type="button"
            onClick={onClose}
            className="rounded border border-gray-300 px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="submit"
            disabled={!name.trim() || save.isPending}
            className="rounded bg-blue-600 px-3 py-1.5 text-sm text-white disabled:opacity-50"
          >
            {t("common:save")}
          </button>
        </div>
      </form>
    </div>
  );
}
