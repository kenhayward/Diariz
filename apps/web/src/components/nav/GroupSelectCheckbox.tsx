import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

/// The group-level select-all checkbox shown in Select mode: checked when every recording in the group is
/// selected, indeterminate when only some are. Toggling selects/deselects the whole group at once.
function GroupSelectCheckbox({
  groupName,
  ids,
  selectedIds,
  onChange,
}: {
  groupName: string;
  ids: string[];
  selectedIds: string[];
  onChange: (selectAll: boolean) => void;
}) {
  const { t } = useTranslation("workspace");
  const ref = useRef<HTMLInputElement>(null);
  const selected = ids.filter((id) => selectedIds.includes(id)).length;
  const all = ids.length > 0 && selected === ids.length;
  const some = selected > 0 && !all;
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = some;
  }, [some]);

  return (
    <input
      ref={ref}
      type="checkbox"
      checked={all}
      aria-label={t("selectAllIn", { section: groupName })}
      onChange={() => onChange(!all)}
      className="shrink-0"
    />
  );
}
export default GroupSelectCheckbox;
