import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { useRoom } from "../lib/rooms";
import { useToast } from "../lib/toast";
import { RoomPermission } from "../lib/types";
import RoomBadge from "./RoomBadge";

/// Pick a shared room to share this recording into. Lists the rooms the caller can add recordings to (holds
/// CreateRecording), excluding the ones the recording is already in. The link lands ungrouped in the target.
export default function ShareToRoomModal({
  recordingId,
  recordingName,
  fromRoomId,
  alreadyInRoomIds,
  onClose,
}: {
  recordingId: string;
  recordingName: string;
  fromRoomId: string;
  alreadyInRoomIds: string[];
  onClose: () => void;
}) {
  const { t } = useTranslation("workspace");
  const { rooms } = useRoom();
  const { showToast } = useToast();
  const qc = useQueryClient();
  const [error, setError] = useState<string | null>(null);

  const targets = rooms.filter(
    (r) => !r.isPersonal && (r.permissions & RoomPermission.CreateRecording) !== 0 && !alreadyInRoomIds.includes(r.id),
  );

  const share = useMutation({
    mutationFn: (toRoomId: string) => api.shareRecordingToRoom(recordingId, fromRoomId, toRoomId),
    onSuccess: (_data, toRoomId) => {
      const room = targets.find((r) => r.id === toRoomId);
      void qc.invalidateQueries({ queryKey: ["recording", recordingId] });
      showToast(t("sharedToRoom", { recording: recordingName, room: room?.name ?? "" }));
      onClose();
    },
    onError: (e) => setError(apiErrorMessage(e)),
  });

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("shareToRoomTitle")}
        onClick={(e) => e.stopPropagation()}
        className="w-full max-w-sm rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{t("shareToRoomTitle")}</h2>
        {error && <p className="mb-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
        {targets.length === 0 ? (
          <p className="text-sm text-gray-500 dark:text-gray-400">{t("shareNoRooms")}</p>
        ) : (
          <ul className="space-y-1">
            {targets.map((r) => (
              <li key={r.id}>
                <button
                  type="button"
                  disabled={share.isPending}
                  onClick={() => share.mutate(r.id)}
                  className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-sm hover:bg-gray-100 disabled:opacity-50 dark:text-gray-100 dark:hover:bg-gray-800"
                >
                  <RoomBadge icon={r.icon} color={r.color} name={r.name} size="xs" />
                  <span className="truncate">{r.name}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
        <div className="mt-4 flex justify-end">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm dark:border-gray-700 dark:text-gray-100 dark:hover:bg-gray-800"
          >
            {t("close")}
          </button>
        </div>
      </div>
    </div>
  );
}
