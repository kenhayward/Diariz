import { createContext, useCallback, useContext, useState, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "./api";
import { titleFromFilename } from "./audioFormats";
import { runUploadBatch, type UploadItem } from "./uploadQueue";
import { useRoom } from "./rooms";

interface UploadContextValue {
  items: UploadItem[];
  /// True while a batch is in flight (any file queued or uploading).
  busy: boolean;
  /// Upload a batch of files; per-file status surfaces in `items`.
  ///
  /// `target` is where the user *put* the files - the folder the list was showing when they dropped them.
  /// Passing it (even as `{ sectionId: null }`, meaning the room root) overrides the placement preference:
  /// a drop is an instruction about this batch, while the preference only answers "where do things go when
  /// I haven't said". The Upload button has no drop target and so omits it.
  uploadFiles: (files: File[], target?: { sectionId: string | null }) => void;
  /// Drop the done/failed rows from the status list (keeps any still in flight).
  clearFinished: () => void;
}

// Default no-op so components can call useUpload() without a provider (e.g. in unit tests).
const UploadContext = createContext<UploadContextValue>({
  items: [],
  busy: false,
  uploadFiles: () => {},
  clearFinished: () => {},
});

/// Shares one upload queue across the top bar's Upload button and the recordings panel's drop zone.
export function UploadProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const { currentRoom, recordingSectionId } = useRoom();
  const [items, setItems] = useState<UploadItem[]>([]);
  const [busy, setBusy] = useState(false);

  const uploadFiles = useCallback(
    (files: File[], target?: { sectionId: string | null }) => {
      if (files.length === 0) return;
      // Snapshot the room and folder at drop time - a batch runs one file at a time and the user can browse
      // away mid-upload, so every file in it is filed where the batch started, not where the panel ends up.
      // Uploading while in a shared room shares the file into it (like recording does) and leaves the main
      // placement ungrouped; otherwise the drop target wins, falling back to the placement preference when
      // there was no drop (the Upload button). Note `target ? ... :` rather than `??`: the target's own
      // `null` means the room root and must not fall through to the preference.
      const intoSharedRoom = !!currentRoom && !currentRoom.isPersonal;
      const roomId = intoSharedRoom ? currentRoom.id : null;
      const sectionId = intoSharedRoom ? null : target ? target.sectionId : recordingSectionId;
      setBusy(true);
      void runUploadBatch(files, {
        upload: async (file) => {
          try {
            await api.uploadFile(file, titleFromFilename(file.name), roomId, sectionId);
          } catch (e) {
            throw new Error(apiErrorMessage(e, "Upload failed."));
          }
        },
        onUpdate: setItems,
        onSuccess: () => qc.invalidateQueries({ queryKey: ["recordings"] }),
      }).finally(() => setBusy(false));
    },
    [qc, currentRoom, recordingSectionId],
  );

  const clearFinished = useCallback(
    () => setItems((it) => it.filter((i) => i.status === "queued" || i.status === "uploading")),
    [],
  );

  return (
    <UploadContext.Provider value={{ items, busy, uploadFiles, clearFinished }}>
      {children}
    </UploadContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useUpload(): UploadContextValue {
  return useContext(UploadContext);
}
