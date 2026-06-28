import { createContext, useCallback, useContext, useState, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "./api";
import { titleFromFilename } from "./audioFormats";
import { runUploadBatch, type UploadItem } from "./uploadQueue";

interface UploadContextValue {
  items: UploadItem[];
  /// True while a batch is in flight (any file queued or uploading).
  busy: boolean;
  /// Upload a batch of files; per-file status surfaces in `items`.
  uploadFiles: (files: File[]) => void;
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
  const [items, setItems] = useState<UploadItem[]>([]);
  const [busy, setBusy] = useState(false);

  const uploadFiles = useCallback(
    (files: File[]) => {
      if (files.length === 0) return;
      setBusy(true);
      void runUploadBatch(files, {
        upload: async (file) => {
          try {
            await api.uploadFile(file, titleFromFilename(file.name));
          } catch (e) {
            throw new Error(apiErrorMessage(e, "Upload failed."));
          }
        },
        onUpdate: setItems,
        onSuccess: () => qc.invalidateQueries({ queryKey: ["recordings"] }),
      }).finally(() => setBusy(false));
    },
    [qc],
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
