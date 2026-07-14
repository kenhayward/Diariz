import { useEffect, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import { groupMeetingTypes, selectedMeetingType } from "../lib/meetingTypes";
import MeetingTypeIcon from "./MeetingTypeIcon";

/// The iconified meeting-type picker. Lists the user's types grouped by their template group, marks the
/// currently-applied one (or the General default when the recording has no explicit type), and applies a chosen
/// type immediately. Disabled while a run is in flight (<c>busy</c>) so the user can't stack runs.
///
/// Two looks, one behaviour: `toolbar` is the quiet bordered button on the Minutes toolbar; `pill` is the
/// purple chip the recording hub's hero card surfaces, where the meeting type is prominent because it drives
/// both the minutes format and which formulas are offered.
export default function MeetingTypeMenu({
  currentTypeId,
  busy,
  onApply,
  variant = "toolbar",
}: {
  currentTypeId: string | null | undefined;
  busy: boolean;
  onApply: (typeId: string) => void;
  variant?: "toolbar" | "pill";
}) {
  const { t } = useTranslation(["workspace"]);
  const { data: types } = useQuery({ queryKey: ["meeting-types"], queryFn: api.listMeetingTypes });
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  // The applied type, else the General default (a recording with no explicit type uses it).
  const selected = useMemo(() => selectedMeetingType(types ?? [], currentTypeId), [types, currentTypeId]);
  // Group by template group, preserving the server's order.
  const groups = useMemo(() => groupMeetingTypes(types ?? []), [types]);

  const disabled = busy || !types || types.length === 0;

  const trigger =
    variant === "pill"
      ? "flex items-center gap-1.5 rounded-full border border-violet-400/40 bg-violet-500/15 px-3 py-1 text-xs font-semibold text-violet-700 hover:bg-violet-500/25 disabled:opacity-50 dark:text-violet-300"
      : "flex items-center gap-1.5 rounded border px-2 py-1 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        aria-label={t("meetingType")}
        aria-haspopup="menu"
        aria-expanded={open}
        disabled={disabled}
        onClick={() => setOpen((v) => !v)}
        className={trigger}
        title={t("meetingTypeHint")}
      >
        {selected && <MeetingTypeIcon icon={selected.icon} color={selected.color} size={16} />}
        <span className="max-w-[10rem] truncate">{selected ? selected.title : t("meetingType")}</span>
        {busy ? (
          <span className="ml-0.5 h-3 w-3 animate-spin rounded-full border-2 border-gray-300 border-t-transparent" aria-hidden />
        ) : (
          <span aria-hidden className="text-xs text-gray-400">▾</span>
        )}
      </button>

      {open && (
        <div
          role="menu"
          className="absolute left-0 z-30 mt-1 max-h-96 w-64 overflow-auto rounded-lg border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900"
        >
          {groups.map(([groupName, list]) => (
            <div key={groupName}>
              <div className="px-3 pt-1.5 pb-0.5 text-xs font-medium uppercase tracking-wide text-gray-400 dark:text-gray-500">
                {groupName}
              </div>
              {list.map((ty) => {
                const isCurrent = ty.id === selected?.id;
                return (
                  <button
                    key={ty.id}
                    type="button"
                    role="menuitemradio"
                    aria-checked={isCurrent}
                    onClick={() => {
                      setOpen(false);
                      if (!isCurrent) onApply(ty.id);
                    }}
                    className={`flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800 ${
                      isCurrent ? "font-medium text-indigo-600 dark:text-indigo-400" : "text-gray-700 dark:text-gray-200"
                    }`}
                  >
                    <MeetingTypeIcon icon={ty.icon} color={ty.color} size={18} />
                    <span className="min-w-0 flex-1 truncate">{ty.title}</span>
                    {isCurrent && <span aria-hidden>✓</span>}
                  </button>
                );
              })}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
