import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import { RoomPermission, RoomPrincipalType, type RoomDetail } from "../lib/types";
import IconColorPicker from "./IconColorPicker";

/// The room permission bits, mirrored from the server enum. Append-only: never renumber.
const ROOM_PERMISSION_BITS = [
  { bit: RoomPermission.ManageRoom, key: "rmPermManageRoom" },
  { bit: RoomPermission.CreateRecording, key: "rmPermCreateRecording" },
  { bit: RoomPermission.RemoveOthersRecordings, key: "rmPermRemoveOthers" },
  { bit: RoomPermission.ShareOut, key: "rmPermShareOut" },
  { bit: RoomPermission.ManageContents, key: "rmPermManageContents" },
  { bit: RoomPermission.EditOthersRecordings, key: "rmPermEditOthers" },
] as const;

const DEFAULT_COLOR = "#5C6BC0";

/// Create, rename, restyle and delete shared rooms, and edit their membership. The Personal room is immutable
/// and not shown here. Reached from the room switcher; visible only to holders of the ManageRooms permission
/// (enforced server-side too). Members' names resolve from the admin user/group lists when available.
export default function ManageRoomsModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const { data: rooms = [] } = useQuery({ queryKey: ["rooms"], queryFn: api.listRooms });
  const { data: users = [] } = useQuery({ queryKey: ["admin-users"], queryFn: api.listUsers });
  const { data: groups = [] } = useQuery({ queryKey: ["groups"], queryFn: api.listGroups });

  const shared = useMemo(() => rooms.filter((r) => !r.isPersonal), [rooms]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  // Keep a selection valid as the list changes.
  useEffect(() => {
    if (selectedId && !shared.some((r) => r.id === selectedId)) setSelectedId(null);
  }, [shared, selectedId]);

  const onError = (e: unknown) => setError(e instanceof Error ? e.message : String(e));

  const create = useMutation({
    mutationFn: (name: string) => api.createRoom({ name }),
    onSuccess: (r) => {
      setSelectedId(r.id);
      void qc.invalidateQueries({ queryKey: ["rooms"] });
    },
    onError,
  });

  function onNewRoom() {
    setError(null);
    // A free "Room N" name the server will accept (names are unique among shared rooms).
    const existing = new Set(shared.map((r) => r.name));
    let n = shared.length + 1;
    while (existing.has(`Room ${n}`)) n++;
    create.mutate(`Room ${n}`);
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
      onClick={onClose}
    >
      <div
        role="dialog"
        aria-label={t("rmTitle")}
        onClick={(e) => e.stopPropagation()}
        className="flex h-[80vh] w-full max-w-4xl flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-center justify-between border-b px-5 py-3 dark:border-gray-700">
          <h2 className="text-base font-semibold dark:text-gray-100">{t("rmTitle")}</h2>
          <button
            type="button"
            aria-label={t("common:close")}
            onClick={onClose}
            className="rounded px-2 text-lg text-gray-400 hover:bg-gray-100 hover:text-gray-700 dark:hover:bg-gray-800"
          >
            ×
          </button>
        </div>

        {error && <p className="px-5 pt-2 text-xs text-red-600 dark:text-red-400">{error}</p>}

        <div className="flex min-h-0 flex-1">
          {/* Left: the shared rooms + New Room. */}
          <div className="flex w-56 shrink-0 flex-col border-r dark:border-gray-700">
            <ul className="min-h-0 flex-1 overflow-y-auto p-2">
              {shared.length === 0 && (
                <li className="px-2 py-2 text-xs text-gray-400 dark:text-gray-500">{t("rmNoRooms")}</li>
              )}
              {shared.map((r) => (
                <li key={r.id}>
                  <button
                    type="button"
                    onClick={() => setSelectedId(r.id)}
                    className={`w-full truncate rounded px-2 py-1.5 text-left text-sm ${
                      selectedId === r.id
                        ? "bg-indigo-50 text-indigo-700 dark:bg-indigo-900/40 dark:text-indigo-200"
                        : "hover:bg-gray-100 dark:text-gray-100 dark:hover:bg-gray-800"
                    }`}
                  >
                    {r.name}
                  </button>
                </li>
              ))}
            </ul>
            <button
              type="button"
              onClick={onNewRoom}
              className="m-2 rounded border px-2 py-1.5 text-sm dark:border-gray-700 dark:text-gray-100 dark:hover:bg-gray-800"
            >
              {t("rmNew")}
            </button>
          </div>

          {/* Right: the selected room's editor. */}
          <div className="min-h-0 flex-1 overflow-y-auto p-4">
            {selectedId ? (
              <RoomEditor
                key={selectedId}
                roomId={selectedId}
                users={users}
                groups={groups}
                onChanged={() => qc.invalidateQueries({ queryKey: ["rooms"] })}
                onDeleted={() => {
                  setSelectedId(null);
                  void qc.invalidateQueries({ queryKey: ["rooms"] });
                }}
                onError={onError}
              />
            ) : (
              <p className="text-sm text-gray-400 dark:text-gray-500">{t("rmPickRoom")}</p>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

function RoomEditor({
  roomId,
  users,
  groups,
  onChanged,
  onDeleted,
  onError,
}: {
  roomId: string;
  users: { id: string; email: string; fullName: string | null }[];
  groups: { id: string; name: string }[];
  onChanged: () => void;
  onDeleted: () => void;
  onError: (e: unknown) => void;
}) {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const { data: room } = useQuery<RoomDetail>({ queryKey: ["room", roomId], queryFn: () => api.getRoom(roomId) });

  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [icon, setIcon] = useState<string | null>(null);
  const [color, setColor] = useState(DEFAULT_COLOR);
  const [confirmName, setConfirmName] = useState("");

  useEffect(() => {
    if (room) {
      setName(room.name);
      setDescription(room.description ?? "");
      setIcon(room.icon);
      setColor(room.color || DEFAULT_COLOR);
      setConfirmName("");
    }
  }, [room]);

  const refetch = () => {
    void qc.invalidateQueries({ queryKey: ["room", roomId] });
    onChanged();
  };

  const save = useMutation({
    mutationFn: () => api.updateRoom(roomId, { name: name.trim(), description: description.trim() || null, icon, color }),
    onSuccess: refetch,
    onError,
  });
  const del = useMutation({ mutationFn: () => api.deleteRoom(roomId), onSuccess: onDeleted, onError });
  const setMember = useMutation({
    mutationFn: (m: { principalType: number; principalId: string; permissions: number }) => api.setRoomMember(roomId, m),
    onSuccess: refetch,
    onError,
  });
  const removeMember = useMutation({
    mutationFn: ({ type, id }: { type: number; id: string }) => api.removeRoomMember(roomId, type, id),
    onSuccess: refetch,
    onError,
  });

  const nameOf = (type: number, id: string) => {
    if (type === RoomPrincipalType.User) {
      const u = users.find((x) => x.id === id);
      return u ? u.fullName || u.email : id;
    }
    return groups.find((g) => g.id === id)?.name ?? id;
  };

  if (!room) return <p className="text-sm text-gray-400 dark:text-gray-500">{t("rmLoading")}</p>;

  const memberKey = (type: number, id: string) => `${type}:${id}`;
  const memberKeys = new Set(room.members.map((m) => memberKey(m.principalType, m.principalId)));
  const addable: { type: number; id: string; label: string }[] = [
    ...users.map((u) => ({ type: RoomPrincipalType.User, id: u.id, label: u.fullName || u.email })),
    ...groups.map((g) => ({ type: RoomPrincipalType.Group, id: g.id, label: g.name })),
  ].filter((c) => !memberKeys.has(memberKey(c.type, c.id)));

  return (
    <div className="space-y-4">
      <label className="block text-sm">
        <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("rmName")}</span>
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
      </label>
      <label className="block text-sm">
        <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("rmDescription")}</span>
        <textarea
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={2}
          className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
      </label>
      <div className="text-sm">
        <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("rmIcon")}</span>
        <IconColorPicker
          icon={icon}
          color={color}
          onChange={(p) => {
            if (p.icon !== undefined) setIcon(p.icon);
            if (p.color !== undefined) setColor(p.color);
          }}
          colorLabel={t("rmColor")}
        />
      </div>
      <button
        type="button"
        onClick={() => save.mutate()}
        disabled={!name.trim()}
        className="rounded bg-indigo-600 px-3 py-1.5 text-sm text-white disabled:opacity-50"
      >
        {t("rmSave")}
      </button>

      {/* Membership. */}
      <div className="border-t pt-3 dark:border-gray-700">
        <h3 className="mb-2 text-sm font-medium dark:text-gray-200">{t("rmMembers")}</h3>
        <div className="space-y-2">
          {room.members.map((m) => (
            <div key={memberKey(m.principalType, m.principalId)} className="rounded border p-2 dark:border-gray-700">
              <div className="mb-1 flex items-center justify-between">
                <span className="text-sm dark:text-gray-100">{nameOf(m.principalType, m.principalId)}</span>
                <button
                  type="button"
                  onClick={() => removeMember.mutate({ type: m.principalType, id: m.principalId })}
                  className="text-xs text-red-600 hover:underline dark:text-red-400"
                >
                  {t("rmRemoveMember")}
                </button>
              </div>
              <div className="flex flex-wrap gap-x-3 gap-y-1">
                {ROOM_PERMISSION_BITS.map(({ bit, key }) => (
                  <label key={key} className="flex items-center gap-1 text-xs text-gray-600 dark:text-gray-300">
                    <input
                      type="checkbox"
                      checked={(m.permissions & bit) !== 0}
                      onChange={() =>
                        setMember.mutate({
                          principalType: m.principalType,
                          principalId: m.principalId,
                          permissions: m.permissions ^ bit,
                        })
                      }
                    />
                    {t(key)}
                  </label>
                ))}
              </div>
            </div>
          ))}
          {room.members.length === 0 && (
            <p className="text-xs text-gray-400 dark:text-gray-500">{t("rmNoMembers")}</p>
          )}
        </div>

        {addable.length > 0 && (
          <select
            aria-label={t("rmAddMember")}
            value=""
            onChange={(e) => {
              const picked = addable.find((c) => memberKey(c.type, c.id) === e.target.value);
              if (picked)
                setMember.mutate({ principalType: picked.type, principalId: picked.id, permissions: RoomPermission.CreateRecording });
            }}
            className="mt-2 w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          >
            <option value="">{t("rmAddMember")}</option>
            {addable.map((c) => (
              <option key={memberKey(c.type, c.id)} value={memberKey(c.type, c.id)}>
                {c.label}
              </option>
            ))}
          </select>
        )}
      </div>

      {/* Danger zone: delete needs the room name typed to confirm. */}
      <div className="border-t pt-3 dark:border-gray-700">
        <label className="block text-sm">
          <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("rmDeleteConfirm", { name: room.name })}</span>
          <input
            value={confirmName}
            onChange={(e) => setConfirmName(e.target.value)}
            placeholder={room.name}
            className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
        </label>
        <button
          type="button"
          onClick={() => del.mutate()}
          disabled={confirmName !== room.name}
          className="mt-2 rounded bg-red-600 px-3 py-1.5 text-sm text-white disabled:opacity-50"
        >
          {t("rmDelete")}
        </button>
      </div>
    </div>
  );
}
