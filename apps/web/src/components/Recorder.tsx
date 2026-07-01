import { useCallback, useEffect, useRef, useState, type ChangeEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage, getToken } from "../lib/api";
import { userIdFromToken } from "../lib/jwt";
import {
  getStream,
  isElectron,
  describeAudioError,
  listInputDevices,
  unlockDeviceLabels,
  type AudioSourceKind,
} from "../lib/audioSource";
import {
  parseSourceToken,
  formatSourceToken,
  buildSourceOptions,
  resolvePersistedSource,
  DEFAULT_CONSTRAINTS,
  type AudioConstraints,
  type InputDevice,
  type PersistedSource,
  type SourceSelection,
} from "../lib/audioDevices";
import { connectTrayRecorder, type RecorderState, type TrayBridge } from "../lib/trayRecorder";
import { AUDIO_ACCEPT_ATTR } from "../lib/audioFormats";
import { useUpload } from "../lib/uploadContext";
import {
  savePendingRecording,
  loadPendingRecording,
  clearPendingRecording,
  type PendingRecording,
} from "../lib/pendingRecording";

const SOURCE_KEY = "diariz.recorder.source";
const CONSTRAINTS_KEY = "diariz.recorder.audioConstraints";

function loadSavedSource(): PersistedSource | null {
  try {
    const raw = localStorage.getItem(SOURCE_KEY);
    return raw ? (JSON.parse(raw) as PersistedSource) : null;
  } catch {
    return null;
  }
}

function loadSavedConstraints(): AudioConstraints {
  try {
    const raw = localStorage.getItem(CONSTRAINTS_KEY);
    return raw ? { ...DEFAULT_CONSTRAINTS, ...(JSON.parse(raw) as Partial<AudioConstraints>) } : DEFAULT_CONSTRAINTS;
  } catch {
    return DEFAULT_CONSTRAINTS;
  }
}

export default function Recorder({
  onUploaded,
  compact = false,
}: {
  onUploaded: () => void;
  compact?: boolean;
}) {
  const { t } = useTranslation("workspace");
  // The chosen source: default mic / a specific mic / system. `selection` carries the deviceId + label
  // so we can survive device-id rotation (see resolvePersistedSource).
  const [selection, setSelection] = useState<SourceSelection>({ kind: "default" });
  const [devices, setDevices] = useState<InputDevice[]>([]);
  const [hasLabels, setHasLabels] = useState(false);
  const [constraints, setConstraints] = useState<AudioConstraints>(DEFAULT_CONSTRAINTS);
  const [cogOpen, setCogOpen] = useState(false);
  const [recording, setRecording] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // An unsaved recording recovered from local storage (its upload failed previously, e.g. the session
  // expired). Offered back for upload so the audio is never lost.
  const [pending, setPending] = useState<PendingRecording | null>(null);

  const userId = userIdFromToken(getToken());

  // On mount, surface any unsaved recording stashed for this user.
  useEffect(() => {
    if (!userId) return;
    let cancelled = false;
    void loadPendingRecording(userId).then((rec) => {
      if (!cancelled && rec) setPending(rec);
    });
    return () => {
      cancelled = true;
    };
  }, [userId]);

  const recorderRef = useRef<MediaRecorder | null>(null);
  const chunksRef = useRef<Blob[]>([]);
  const startRef = useRef(0);
  const timerRef = useRef<number | null>(null);
  // The coarse source actually being recorded (mic vs system); the tray only speaks in these terms,
  // and the upload title/enum needs it, so we can't rely on `selection` state having flushed.
  const activeSourceRef = useRef<AudioSourceKind>("mic");
  // Reports phase changes to the Electron tray; a no-op in a plain browser.
  const reportRef = useRef<(s: RecorderState) => void>(() => {});
  // Wraps the ⚙ button + its popover, so an outside click / Escape can close it.
  const cogRef = useRef<HTMLDivElement>(null);

  // Re-enumerate inputs (mount, hot-plug via devicechange, and after a grant unlocks labels). Also
  // re-resolves a specific-mic selection against the new list so an unplugged device falls back cleanly.
  const refreshDevices = useCallback(async () => {
    const list = await listInputDevices().catch(() => ({ devices: [], hasLabels: false }));
    setDevices(list.devices);
    setHasLabels(list.hasLabels);
    setSelection((cur) =>
      cur.kind === "device"
        ? resolvePersistedSource({ token: formatSourceToken(cur), label: cur.label }, list.devices)
        : cur,
    );
  }, []);

  // On mount: restore persisted source + constraints, enumerate devices, subscribe to hot-plug.
  useEffect(() => {
    setConstraints(loadSavedConstraints());
    const saved = loadSavedSource();
    let cancelled = false;
    void (async () => {
      // Always enumerate — `enumerateDevices()` never triggers a permission prompt, and it returns
      // real labels whenever this origin already has mic access (e.g. after recording once). We do NOT
      // gate on navigator.permissions: that query is unreliable (returns "prompt" in Electron and some
      // browsers even when access is granted and labels are available), which would wrongly hide the
      // user's connected mics. If labels are genuinely withheld, hasLabels stays false and the
      // "Allow microphone…" affordance handles unlocking them.
      const list = await listInputDevices().catch(() => ({
        devices: [] as InputDevice[],
        hasLabels: false,
      }));
      if (cancelled) return;
      setDevices(list.devices);
      setHasLabels(list.hasLabels);
      setSelection(resolvePersistedSource(saved, list.devices));
    })();

    const md = navigator.mediaDevices;
    const onChange = () => void refreshDevices();
    md?.addEventListener?.("devicechange", onChange);
    return () => {
      cancelled = true;
      md?.removeEventListener?.("devicechange", onChange);
    };
  }, [refreshDevices]);

  // Close the audio-settings popover on an outside click or Escape (same pattern as KebabMenu).
  useEffect(() => {
    if (!cogOpen) return;
    function onDown(e: MouseEvent) {
      if (cogRef.current && !cogRef.current.contains(e.target as Node)) setCogOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setCogOpen(false);
    }
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [cogOpen]);

  function persistSource(sel: SourceSelection) {
    try {
      localStorage.setItem(SOURCE_KEY, JSON.stringify({ token: formatSourceToken(sel), label: sel.label }));
    } catch {
      /* storage unavailable — non-fatal */
    }
  }

  function onSelectSource(e: ChangeEvent<HTMLSelectElement>) {
    const sel = parseSourceToken(e.target.value);
    if (sel.kind === "device") sel.label = devices.find((d) => d.deviceId === sel.deviceId)?.label || undefined;
    setSelection(sel);
    persistSource(sel);
    if (sel.kind === "system") setCogOpen(false); // constraints don't apply to loopback
  }

  function toggleConstraint(key: keyof AudioConstraints) {
    setConstraints((c) => {
      const next = { ...c, [key]: !c[key] };
      try {
        localStorage.setItem(CONSTRAINTS_KEY, JSON.stringify(next));
      } catch {
        /* non-fatal */
      }
      return next;
    });
  }

  async function allowMicLabels() {
    setError(null);
    try {
      await unlockDeviceLabels();
      await refreshDevices();
    } catch (e) {
      setError(describeAudioError(e, "mic", isElectron));
    }
  }

  // `trayKind` is set only when the Electron tray drives us (it speaks coarse mic/system); the on-screen
  // button passes nothing and records the current `selection`. A tray "mic" maps to the current specific
  // mic (or default), "system" to loopback.
  async function start(trayKind?: AudioSourceKind) {
    let sel: SourceSelection;
    if (trayKind === "system") sel = { kind: "system" };
    else if (trayKind === "mic") sel = selection.kind === "system" ? { kind: "default" } : selection;
    else sel = selection;

    const coarse: AudioSourceKind = sel.kind === "system" ? "system" : "mic";
    setError(null);
    try {
      const stream = await getStream(sel, coarse === "mic" ? constraints : undefined);
      const recorder = new MediaRecorder(stream, { mimeType: "audio/webm" });
      chunksRef.current = [];
      recorder.ondataavailable = (e) => e.data.size > 0 && chunksRef.current.push(e.data);
      recorder.onstop = () => {
        stream.getTracks().forEach((t) => t.stop());
        void upload();
      };
      recorder.start();
      recorderRef.current = recorder;
      activeSourceRef.current = coarse;
      startRef.current = Date.now();
      setElapsed(0);
      timerRef.current = window.setInterval(
        () => setElapsed(Date.now() - startRef.current),
        250,
      );
      setRecording(true);
      reportRef.current({ phase: "recording", source: coarse });
      // A mic grant unlocks device labels — re-enumerate so specifics appear next time.
      if (coarse === "mic") void refreshDevices();
    } catch (e) {
      // Log the raw cause (DOMException name/message) so the actual failure is diagnosable.
      console.error("Audio capture failed:", e);
      const message = describeAudioError(e, coarse, isElectron);
      setError(message);
      reportRef.current({ phase: "error", error: message });
    }
  }

  function stop() {
    if (timerRef.current) window.clearInterval(timerRef.current);
    setRecording(false);
    recorderRef.current?.stop();
  }

  async function upload() {
    setBusy(true);
    reportRef.current({ phase: "uploading" });
    const blob = new Blob(chunksRef.current, { type: "audio/webm" });
    const durationMs = Date.now() - startRef.current;
    const source: "Microphone" | "System" = activeSourceRef.current === "system" ? "System" : "Microphone";
    const prefix = source === "System" ? t("recTitlePrefixSystem") : t("recTitlePrefixMic");
    const title = `${prefix} ${new Date().toLocaleString()}`;
    const rec: PendingRecording = { userId: userId ?? "", blob, title, durationMs, source, createdAt: Date.now() };

    // Stash the audio BEFORE uploading. If the upload fails (e.g. an expired session redirects to login),
    // the recording survives in local storage and is offered for re-upload on the next visit.
    if (userId) await savePendingRecording(rec);

    try {
      await api.upload(blob, title, durationMs, source);
      if (userId) await clearPendingRecording(userId);
      setPending(null);
      onUploaded();
      reportRef.current({ phase: "idle" });
    } catch (e) {
      const message = apiErrorMessage(e, t("errUpload"));
      setError(message);
      if (userId) setPending(rec); // safe in storage — show the recovery banner
      reportRef.current({ phase: "error", error: message });
    } finally {
      setBusy(false);
    }
  }

  async function uploadPending() {
    if (!pending) return;
    setBusy(true);
    setError(null);
    try {
      await api.upload(pending.blob, pending.title, pending.durationMs, pending.source);
      if (userId) await clearPendingRecording(userId);
      setPending(null);
      onUploaded();
    } catch (e) {
      setError(apiErrorMessage(e, t("errUpload")));
    } finally {
      setBusy(false);
    }
  }

  async function discardPending() {
    if (!window.confirm(t("confirmDiscardRecording"))) return;
    if (userId) await clearPendingRecording(userId);
    setPending(null);
  }

  // Upload existing audio files (the "Upload" button). The shared upload queue handles validation,
  // per-file status, and refreshing the list; you can also drag files onto the recordings panel.
  const uploads = useUpload();
  const fileRef = useRef<HTMLInputElement>(null);
  function onPickFiles(e: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(e.target.files ?? []);
    e.target.value = ""; // let the user re-pick the same file later
    uploads.uploadFiles(files);
  }

  // Keep the tray bridge pointed at the latest start/stop without reconnecting.
  const startFn = useRef(start);
  startFn.current = start;
  const stopFn = useRef(stop);
  stopFn.current = stop;

  // Connect the Electron tray to this (single) recorder instance. Tray "start"/"stop"
  // drive the same recorder as the on-screen button; we report phase back so the tray
  // shows the live timer and raises notifications. No-op outside the desktop shell.
  useEffect(() => {
    const diariz = (window as unknown as { diariz?: TrayBridge }).diariz;
    const conn = connectTrayRecorder(diariz, {
      onStart: (src) => void startFn.current(src),
      onStop: () => stopFn.current(),
    });
    reportRef.current = conn.reportState;
    return () => {
      conn.dispose();
      reportRef.current = () => {};
    };
  }, []);

  const secs = Math.floor(elapsed / 1000);
  const mmss = `${String(Math.floor(secs / 60)).padStart(2, "0")}:${String(secs % 60).padStart(2, "0")}`;

  return (
    <div className={compact ? "" : "rounded-lg border bg-white p-4 dark:border-gray-700 dark:bg-gray-900"}>
      <div className="flex items-center gap-2">
        <select
          value={formatSourceToken(selection)}
          onChange={onSelectSource}
          disabled={recording}
          aria-label={t("sourceMicrophone")}
          className="rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        >
          {buildSourceOptions(devices, hasLabels, {
            micDefault: t("sourceMicDefault"),
            system: `${t("sourceSystem")}${isElectron ? "" : t("systemDesktopSuffix")}`,
            numbered: (n) => t("sourceMicNumbered", { n }),
          }).map((o) => (
            <option key={o.token} value={o.token}>
              {o.label}
            </option>
          ))}
        </select>

        {/* Capture-constraint popover — mic only (loopback ignores these). */}
        <div className="relative" ref={cogRef}>
          <button
            type="button"
            onClick={() => setCogOpen((o) => !o)}
            disabled={recording || selection.kind === "system"}
            title={t("audioSettings")}
            aria-label={t("audioSettings")}
            aria-expanded={cogOpen}
            className="rounded border px-2 py-1 text-sm disabled:opacity-40 dark:border-gray-700 dark:text-gray-100 dark:hover:bg-gray-800"
          >
            ⚙
          </button>
          {cogOpen && selection.kind !== "system" && (
            <div className="absolute left-0 top-full z-20 mt-1 w-56 rounded border bg-white p-3 text-sm shadow-lg dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100">
              <div className="mb-2 flex items-center justify-between">
                <span className="text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">
                  {t("audioSettings")}
                </span>
                <button
                  type="button"
                  onClick={() => setCogOpen(false)}
                  aria-label={t("close")}
                  title={t("close")}
                  className="rounded px-1 text-gray-500 hover:bg-gray-100 hover:text-gray-800 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-100"
                >
                  ✕
                </button>
              </div>
              {(
                [
                  ["echoCancellation", t("constraintEcho")],
                  ["noiseSuppression", t("constraintNoise")],
                  ["autoGainControl", t("constraintAgc")],
                  ["mono", t("constraintMono")],
                ] as const
              ).map(([key, label]) => (
                <label key={key} className="flex items-center gap-2 py-1">
                  <input
                    type="checkbox"
                    checked={constraints[key]}
                    onChange={() => toggleConstraint(key)}
                  />
                  <span>{label}</span>
                </label>
              ))}
              <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">{t("constraintsMicOnlyHint")}</p>
            </div>
          )}
        </div>

        {!hasLabels && !recording && (
          <button
            type="button"
            onClick={allowMicLabels}
            className="text-xs text-blue-600 underline hover:text-blue-700 dark:text-blue-400"
          >
            {t("allowMicToList")}
          </button>
        )}

        {recording ? (
          <button onClick={stop} className="rounded bg-red-600 px-3 py-1.5 text-sm text-white">
            {t("recStop")}
          </button>
        ) : (
          <button
            onClick={() => start()}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? t("recUploading") : t("recRecord")}
          </button>
        )}

        <button
          type="button"
          onClick={() => fileRef.current?.click()}
          disabled={recording || busy}
          title={t("recUploadTitle")}
          className="rounded border px-3 py-1.5 text-sm disabled:opacity-50 dark:border-gray-700 dark:text-gray-100 dark:hover:bg-gray-800"
        >
          {t("recUpload")}
        </button>
        <input
          ref={fileRef}
          type="file"
          accept={AUDIO_ACCEPT_ATTR}
          multiple
          onChange={onPickFiles}
          className="hidden"
          data-testid="upload-input"
        />

        {recording && <span className="font-mono text-sm text-red-600">● {mmss}</span>}
        {error && compact && <span className="text-xs text-red-600">{error}</span>}
      </div>
      {pending && !recording && (
        <div className="mt-2 flex flex-wrap items-center gap-2 rounded border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/30 dark:text-amber-200">
          <span>{t("unsavedRecording", { time: new Date(pending.createdAt).toLocaleString() })}</span>
          <div className="ml-auto flex gap-2">
            <button
              type="button"
              onClick={uploadPending}
              disabled={busy}
              className="rounded bg-amber-600 px-2 py-1 text-xs text-white disabled:opacity-50"
            >
              {busy ? t("recUploading") : t("recUploadPending")}
            </button>
            <button
              type="button"
              onClick={discardPending}
              disabled={busy}
              className="rounded border border-amber-400 px-2 py-1 text-xs disabled:opacity-50 dark:border-amber-700"
            >
              {t("recDiscardPending")}
            </button>
          </div>
        </div>
      )}
      {error && !compact && <p className="mt-2 text-sm text-red-600">{error}</p>}
    </div>
  );
}
