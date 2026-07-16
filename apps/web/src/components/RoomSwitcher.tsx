import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { useRoom } from "../lib/rooms";
import { useAuth } from "../auth";
import type { RoomListItem } from "../lib/types";
import Avatar from "./Avatar";
import RoomBadge from "./RoomBadge";
import ManageRoomsModal from "./ManageRoomsModal";
import { HomeIcon } from "./icons";

/// A small icon for a room: the signed-in user's avatar for their Personal room, else the shared room's chosen
/// icon (or a colour swatch with its first letter when none was picked).
function RoomIcon({ room, size = "sm" }: { room: RoomListItem; size?: "xs" | "sm" }) {
  const { initials, pictureUrl } = useAuth();
  if (room.isPersonal) return <Avatar initials={initials} pictureUrl={pictureUrl} size={size} />;
  return <RoomBadge icon={room.icon} color={room.color} name={room.name} size={size} />;
}

/// The left-panel header: a room switcher (current room's icon + name, a dropdown of the rooms the user belongs
/// to, personal first) plus the panel collapse control. Replaces the old static "Meetings" header. With one
/// room today the dropdown has a single entry; it becomes load-bearing once shared rooms arrive.
export default function RoomSwitcher({ onCollapse, chevron }: { onCollapse: () => void; chevron: string }) {
  const { t } = useTranslation("workspace");
  const { rooms, currentRoom } = useRoom();
  const { permissions } = useAuth();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [manageOpen, setManageOpen] = useState(false);
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

  function select(room: RoomListItem) {
    setOpen(false);
    if (room.id !== currentRoom?.id) navigate(`/rooms/${room.id}`);
  }

  return (
    <div className="flex h-9 shrink-0 items-center justify-between gap-1 border-b px-2 dark:border-gray-700">
      <div className="relative min-w-0 flex-1" ref={ref}>
        <button
          type="button"
          aria-haspopup="menu"
          aria-expanded={open}
          aria-label={t("switchRoom")}
          onClick={() => setOpen((v) => !v)}
          className="flex w-full min-w-0 items-center gap-1.5 rounded px-1 py-0.5 hover:bg-gray-100 dark:hover:bg-gray-800"
        >
          {currentRoom && <RoomIcon room={currentRoom} size="xs" />}
          <span className="min-w-0 flex-1 truncate text-left text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400">
            {currentRoom?.name ?? ""}
          </span>
          <span aria-hidden="true" className="text-[10px] text-gray-400">
            ▾
          </span>
        </button>

        {open && (
          <div
            role="menu"
            className="absolute left-0 z-50 mt-1 w-56 overflow-hidden rounded-lg border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900"
          >
            <p className="px-3 py-1 text-[10px] font-semibold uppercase tracking-wide text-gray-400 dark:text-gray-500">
              {t("yourRooms")}
            </p>
            {rooms.map((r) => (
              <button
                key={r.id}
                type="button"
                role="menuitem"
                // Marks the room you're in without a second visual language: the checkmark is the only
                // decoration, so scanning the list is about the names.
                aria-current={r.id === currentRoom?.id ? "true" : undefined}
                onClick={() => select(r)}
                className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm hover:bg-gray-100 dark:hover:bg-gray-800 dark:text-gray-100"
              >
                <RoomIcon room={r} />
                <span className="min-w-0 flex-1">
                  <span className="block truncate">{r.name}</span>
                  {/* What's actually in there. "shared" leads the line because it is the one thing a name
                      can't tell you, and it decides who else can read what's inside. */}
                  <span className="block truncate text-[10px] text-gray-500 dark:text-gray-400">
                    {[
                      r.isPersonal ? null : t("roomSharedPrefix"),
                      t("roomCounts", { count: r.sectionCount }),
                      t("roomRecordings", { count: r.recordingCount }),
                    ]
                      .filter(Boolean)
                      .join(" · ")}
                  </span>
                </span>
                {r.id === currentRoom?.id && (
                  <span aria-hidden="true" className="shrink-0 text-xs text-blue-600 dark:text-blue-400">
                    ✓
                  </span>
                )}
              </button>
            ))}
            {permissions.manageRooms && (
              <>
                <hr className="my-1 border-gray-200 dark:border-gray-700" />
                <button
                  type="button"
                  role="menuitem"
                  onClick={() => {
                    setOpen(false);
                    setManageOpen(true);
                  }}
                  className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm hover:bg-gray-100 dark:hover:bg-gray-800 dark:text-gray-100"
                >
                  <span className="shrink-0 text-gray-500 dark:text-gray-400">
                    <HomeIcon size={14} />
                  </span>
                  <span className="truncate">{t("manageRooms")}</span>
                </button>
              </>
            )}
          </div>
        )}
      </div>

      {manageOpen && <ManageRoomsModal onClose={() => setManageOpen(false)} />}

      <button
        type="button"
        aria-label={t("collapsePanel", { title: currentRoom?.name ?? "" })}
        onClick={onCollapse}
        className="rounded px-1 text-xs text-gray-400 hover:bg-gray-100 hover:text-gray-700 dark:hover:bg-gray-800 dark:hover:text-gray-200"
      >
        {chevron}
      </button>
    </div>
  );
}
