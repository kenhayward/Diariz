import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { useRoom } from "../lib/rooms";
import { useAuth } from "../auth";
import type { RoomListItem } from "../lib/types";
import Avatar from "./Avatar";
import ManageRoomsModal from "./ManageRoomsModal";

/// A small icon for a room: the signed-in user's avatar for their Personal room, else a rounded square in the
/// room's colour bearing its first letter. (The full icon picker lands with Manage Rooms in Phase 4.)
function RoomIcon({ room }: { room: RoomListItem }) {
  const { initials, pictureUrl } = useAuth();
  if (room.isPersonal) return <Avatar initials={initials} pictureUrl={pictureUrl} />;
  return (
    <span
      className="flex h-8 w-8 items-center justify-center rounded-md text-xs font-medium text-white"
      style={{ backgroundColor: room.color ?? "#6b7280" }}
      aria-hidden="true"
    >
      {room.name.trim().charAt(0).toUpperCase() || "?"}
    </span>
  );
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
    <div className="flex h-9 shrink-0 items-center justify-between border-b px-2 dark:border-gray-700">
      <div className="relative min-w-0" ref={ref}>
        <button
          type="button"
          aria-haspopup="menu"
          aria-expanded={open}
          aria-label={t("switchRoom")}
          onClick={() => setOpen((v) => !v)}
          className="flex min-w-0 items-center gap-1.5 rounded px-1 py-0.5 hover:bg-gray-100 dark:hover:bg-gray-800"
        >
          {currentRoom && <RoomIcon room={currentRoom} />}
          <span className="max-w-[9rem] truncate text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400">
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
            {rooms.map((r) => (
              <button
                key={r.id}
                type="button"
                role="menuitem"
                onClick={() => select(r)}
                className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm hover:bg-gray-100 dark:hover:bg-gray-800 dark:text-gray-100"
              >
                <RoomIcon room={r} />
                <span className="truncate">{r.name}</span>
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
                  <span aria-hidden="true">⌂</span>
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
