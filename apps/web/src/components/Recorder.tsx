import { useCallback, useEffect, useRef, useState, type ChangeEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage, getToken } from "../lib/api";
import { userIdFromToken } from "../lib/jwt";
import {
  getStream,
  getCombinedStream,
  supportsDisplayAudio,
  isElectron,
  describeAudioError,
  listInputDevices,
  unlockDeviceLabels,
  type AudioSourceKind,
  type CaptureSession,
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
import InputLevelMeter from "./InputLevelMeter";
import { AUDIO_ACCEPT_ATTR } from "../lib/audioFormats";
import { useUpload } from "../lib/uploadContext";
import {
  savePendingRecording,
  loadPendingRecording,
  clearPendingRecording,
  type PendingRecording,
} from "../lib/pendingRecording";
import * as timing from "../lib/recorderTiming";
import type { Timing } from "../lib/recorderTiming";
import {
  savePendingNotes,
  loadPendingNotes,
  clearPendingNotes,
  type PendingNotes,
} from "../lib/pendingNotes";
import LiveNotesPanel from "./LiveNotesPanel";
import type { MeetingNote, RecordingSource } from "../lib/types";

const SOURCE_KEY = "diariz.recorder.source";
const CONSTRAINTS_KEY = "diariz.recorder.audioConstraints";
const SYSTEM_AUDIO_KEY = "diariz.recorder.systemAudio";

// Whether this environment can capture system audio at all (Chromium/desktop). Drives the System audio
// checkbox + the "No microphone" dropdown option; false in Firefox/Safari.
const CAN_SYSTEM_AUDIO = supportsDisplayAudio() || isElectron;

function loadSavedSource(): PersistedSource | null {
  try {
    const raw = localStorage.getItem(SOURCE_KEY);
    return raw ? (JSON.parse(raw) as PersistedSource) : null;
  } catch {
    return null;
  }
}

function loadSavedSystemAudio(): boolean {
  try {
    return localStorage.getItem(SYSTEM_AUDIO_KEY) === "true";
  } catch {
    return false;
  }
}

// A share dialog that was cancelled/denied, or that returned no audio track. When a mic is also being
// captured we can safely fall back to mic-only; a hard failure (e.g. NotReadableError) is rethrown.
function isAbortish(e: unknown): boolean {
  const name = (e as { name?: string } | null)?.name;
  return (
    name === "NotAllowedError" ||
    name === "NotFoundError" ||
    name === "AbortError" ||
    name === "SecurityError" ||
    name === "PermissionDeniedError"
  );
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
  // Add system audio to the capture (mixed with the mic, or on its own when "No microphone" is chosen).
  const [systemAudio, setSystemAudio] = useState(false);
  const [devices, setDevices] = useState<InputDevice[]>([]);
  const [hasLabels, setHasLabels] = useState(false);
  const [constraints, setConstraints] = useState<AudioConstraints>(DEFAULT_CONSTRAINTS);
  const [cogOpen, setCogOpen] = useState(false);
  // True once we've asked the browser for mic access to reveal device labels (on first picker focus).
  // Used to show the "no microphone detected" hint only after an attempt that came back empty.
  const [labelsTried, setLabelsTried] = useState(false);
  const [recording, setRecording] = useState(false);
  // Paused mid-recording: capture is suspended (nothing recorded, mic muted) but the recorder is still live.
  const [paused, setPaused] = useState(false);
  // True once the input has been near-silent for a sustained period while recording (see InputLevelMeter).
  const [silent, setSilent] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // A non-fatal notice (e.g. we fell back to mic-only because system audio wasn't shared).
  const [notice, setNotice] = useState<string | null>(null);
  // An unsaved recording recovered from local storage (its upload failed previously, e.g. the session
  // expired). Offered back for upload so the audio is never lost.
  const [pending, setPending] = useState<PendingRecording | null>(null);
  // Live notes taken while recording: local lines (fake ids) stamped with the *recorded* clock, mirrored to
  // IndexedDB so a crash never loses them, and attached to the recording after upload.
  const [liveLines, setLiveLines] = useState<MeetingNote[]>([]);
  const [notesOpen, setNotesOpen] = useState(false);
  // Lines whose audio uploaded but whose attach failed (durable, with the recording id) - drives the retry banner.
  const [notesAttach, setNotesAttach] = useState<PendingNotes | null>(null);

  const userId = userIdFromToken(getToken());

  // On mount, surface any unsaved recording stashed for this user - and any note lines whose audio uploaded
  // but whose attach failed (they carry the recording id, so the banner can retry).
  useEffect(() => {
    if (!userId) return;
    let cancelled = false;
    void loadPendingRecording(userId).then((rec) => {
      if (!cancelled && rec) setPending(rec);
    });
    void loadPendingNotes(userId).then((stash) => {
      if (!cancelled && stash && stash.lines.length > 0 && stash.recordingId) setNotesAttach(stash);
    });
    return () => {
      cancelled = true;
    };
  }, [userId]);

  const recorderRef = useRef<MediaRecorder | null>(null);
  // The active capture (its stream is recorded + metered; `stop` tears down all tracks + any AudioContext).
  const sessionRef = useRef<CaptureSession | null>(null);
  // The live recording stream, exposed to the level meter while recording (nulled in recorder.onstop).
  const streamRef = useRef<MediaStream | null>(null);
  // Latest checkbox value, read inside start() (state may not have flushed when a tray command fires).
  const systemAudioRef = useRef(false);
  const chunksRef = useRef<Blob[]>([]);
  // Tracks *recorded* time (excludes paused stretches) so the timer + uploaded duration stay honest.
  const timingRef = useRef<Timing>({ accumulatedMs: 0, runningSince: null });
  // Read inside upload() (state may not have flushed when onstop fires).
  const liveLinesRef = useRef<MeetingNote[]>([]);
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

  // Keep the ref in step with the checkbox so a tray-driven start() reads the latest value.
  useEffect(() => {
    systemAudioRef.current = systemAudio;
  }, [systemAudio]);

  // On mount: restore persisted source + constraints, enumerate devices, subscribe to hot-plug.
  useEffect(() => {
    setConstraints(loadSavedConstraints());
    setSystemAudio(CAN_SYSTEM_AUDIO && loadSavedSystemAudio());
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
      const restored = resolvePersistedSource(saved, list.devices);
      // "No microphone" is only usable when system audio is available; otherwise it would strand Record.
      setSelection(restored.kind === "none" && !CAN_SYSTEM_AUDIO ? { kind: "default" } : restored);
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
    if (sel.kind === "none") setCogOpen(false); // no mic to tune
  }

  function toggleSystemAudio(on: boolean) {
    setSystemAudio(on);
    try {
      localStorage.setItem(SYSTEM_AUDIO_KEY, String(on));
    } catch {
      /* non-fatal */
    }
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

  // When the user opens the source picker and we don't yet have device labels, ask the browser for mic
  // access once — this is the natural permission prompt (no bespoke "Allow…" link needed). On success we
  // re-enumerate so specific mics appear; on failure the "no microphone" hint explains it. We deliberately
  // don't raise a red capture error just from focusing the picker.
  async function ensureDeviceLabels() {
    if (hasLabels || labelsTried) return;
    setLabelsTried(true);
    try {
      await unlockDeviceLabels();
      await refreshDevices();
    } catch {
      /* denied or no device — the empty-state hint covers it */
    }
  }

  function startTicker() {
    if (timerRef.current) window.clearInterval(timerRef.current);
    timerRef.current = window.setInterval(
      () => setElapsed(timing.elapsedMs(timingRef.current, Date.now())),
      250,
    );
  }

  function stopTicker() {
    if (timerRef.current) window.clearInterval(timerRef.current);
    timerRef.current = null;
  }

  // Mute/unmute the live capture tracks. While paused we disable them so nothing is captured *and* the
  // level meter visibly flatlines — a clear "you're not being recorded" signal for sensitive moments.
  function setCaptureEnabled(on: boolean) {
    streamRef.current?.getAudioTracks?.().forEach((tr) => {
      tr.enabled = on;
    });
  }

  // ---- Live notes (taken while recording; attached to the recording after upload) ----

  const NOTES_OPEN_KEY = "diariz.recorder.notesOpen";

  /// Update the local lines and mirror them to IndexedDB (recordingId null = still recording).
  function mirrorLines(lines: MeetingNote[]) {
    liveLinesRef.current = lines;
    setLiveLines(lines);
    if (userId)
      void savePendingNotes({
        userId,
        recordingId: null,
        updatedAt: Date.now(),
        lines: lines.map((l) => ({ text: l.text, capturedAtMs: l.capturedAtMs })),
      });
  }

  function addLiveNote(text: string) {
    const line: MeetingNote = {
      id: crypto.randomUUID(),
      text,
      capturedAtMs: timing.elapsedMs(timingRef.current, Date.now()),
      ordinal: liveLinesRef.current.length,
      createdAt: new Date().toISOString(),
    };
    mirrorLines([...liveLinesRef.current, line]);
  }

  function editLiveNote(id: string, text: string) {
    mirrorLines(liveLinesRef.current.map((l) => (l.id === id ? { ...l, text } : l)));
  }

  function deleteLiveNote(id: string) {
    mirrorLines(liveLinesRef.current.filter((l) => l.id !== id));
  }

  function closeNotes() {
    setNotesOpen(false);
    try {
      localStorage.setItem(NOTES_OPEN_KEY, "false");
    } catch {
      /* non-fatal */
    }
  }

  function openNotes() {
    setNotesOpen(true);
    try {
      localStorage.setItem(NOTES_OPEN_KEY, "true");
    } catch {
      /* non-fatal */
    }
  }

  /// Attach lines to the created recording. Success clears the durable stash; failure keeps the lines (with
  /// the recording id) and surfaces the retry banner. A notes failure never fails the upload itself.
  async function attachNotes(recordingId: string, fromRetry?: PendingNotes) {
    const lines = fromRetry
      ? fromRetry.lines
      : liveLinesRef.current.map((l) => ({ text: l.text, capturedAtMs: l.capturedAtMs }));
    if (lines.length === 0) {
      if (userId) void clearPendingNotes(userId);
      return;
    }
    try {
      await api.createNotes(recordingId, lines);
      if (userId) await clearPendingNotes(userId);
      liveLinesRef.current = [];
      setLiveLines([]);
      setNotesAttach(null);
    } catch {
      const stash: PendingNotes = { userId: userId ?? "", recordingId, lines, updatedAt: Date.now() };
      if (userId) await savePendingNotes(stash);
      setNotesAttach(stash);
    }
  }

  // `trayKind` is set only when the Electron tray drives us (it speaks coarse mic/system); the on-screen
  // button passes nothing and records the current `selection`. A tray "mic" maps to the current specific
  // mic (or default), "system" to loopback.
  async function start(trayKind?: AudioSourceKind) {
    // Resolve which mic (if any) and whether to add system audio, from a tray command or the on-screen
    // controls. A tray "mic" with the current "No microphone" selection falls back to the default mic.
    let micSel: SourceSelection;
    let wantMic: boolean;
    let wantSystem: boolean;
    const asMic = () => (selection.kind === "none" ? ({ kind: "default" } as SourceSelection) : selection);
    if (trayKind === "mic") {
      wantMic = true; wantSystem = false; micSel = asMic();
    } else if (trayKind === "system") {
      wantMic = false; wantSystem = true; micSel = { kind: "default" };
    } else if (trayKind === "both") {
      wantMic = true; wantSystem = true; micSel = asMic();
    } else {
      wantMic = selection.kind !== "none"; wantSystem = systemAudioRef.current; micSel = selection;
    }
    if (!wantMic && !wantSystem) return; // nothing selected (Record is disabled, but guard anyway)

    setError(null);
    setNotice(null);
    let coarse: AudioSourceKind = wantMic && wantSystem ? "both" : wantMic ? "mic" : "system";
    try {
      let session: CaptureSession;
      if (wantMic && wantSystem) {
        try {
          session = await getCombinedStream(micSel, constraints);
        } catch (e) {
          if (!isAbortish(e)) throw e;
          // System audio wasn't shared - record the mic alone rather than losing the take.
          session = await getStream(micSel, constraints);
          coarse = "mic";
          setNotice(t("combinedFellBackToMic"));
        }
      } else if (wantMic) {
        session = await getStream(micSel, constraints);
      } else {
        session = await getStream({ kind: "system" }, undefined);
      }

      sessionRef.current = session;
      streamRef.current = session.stream; // exposed so the level meter can tap it while recording
      const recorder = new MediaRecorder(session.stream, { mimeType: "audio/webm" });
      chunksRef.current = [];
      recorder.ondataavailable = (e) => e.data.size > 0 && chunksRef.current.push(e.data);
      recorder.onstop = () => {
        sessionRef.current?.stop();
        sessionRef.current = null;
        streamRef.current = null;
        void upload();
      };
      recorder.start();
      recorderRef.current = recorder;
      activeSourceRef.current = coarse;
      timingRef.current = timing.start(Date.now());
      setElapsed(0);
      startTicker();
      setRecording(true);
      setPaused(false);
      // Fresh notes for a fresh recording: clear any stale unattached lines (orphans from a crash whose
      // audio never reached Stop - there is nothing to attach them to) and open the panel per preference.
      liveLinesRef.current = [];
      setLiveLines([]);
      if (userId) void clearPendingNotes(userId);
      setNotesOpen(localStorage.getItem(NOTES_OPEN_KEY) !== "false");
      reportRef.current({ phase: "recording", source: coarse });
      // A mic grant unlocks device labels — re-enumerate so specifics appear next time.
      if (coarse !== "system") void refreshDevices();
    } catch (e) {
      // Log the raw cause (DOMException name/message) so the actual failure is diagnosable.
      console.error("Audio capture failed:", e);
      const message = describeAudioError(e, coarse, isElectron);
      setError(message);
      reportRef.current({ phase: "error", error: message });
    }
  }

  // Suspend capture without ending the recording: paused audio is never captured (the model never sees
  // it), the mic is muted, and the recorded-time clock stops so the duration stays honest.
  function pause() {
    const rec = recorderRef.current;
    if (!rec || rec.state !== "recording") return;
    rec.pause();
    timingRef.current = timing.pause(timingRef.current, Date.now());
    stopTicker();
    setElapsed(timing.elapsedMs(timingRef.current, Date.now()));
    setCaptureEnabled(false);
    setSilent(false);
    setPaused(true);
  }

  function resume() {
    const rec = recorderRef.current;
    if (!rec || rec.state !== "paused") return;
    setCaptureEnabled(true);
    rec.resume();
    timingRef.current = timing.resume(timingRef.current, Date.now());
    startTicker();
    setPaused(false);
  }

  function stop() {
    stopTicker();
    // Fold any running segment so the uploaded duration is final and paused-free.
    timingRef.current = timing.pause(timingRef.current, Date.now());
    setRecording(false);
    setPaused(false);
    setSilent(false);
    recorderRef.current?.stop();
  }

  async function upload() {
    setBusy(true);
    reportRef.current({ phase: "uploading" });
    const blob = new Blob(chunksRef.current, { type: "audio/webm" });
    // Recorded time only (pauses excluded); stop() has already folded the final running segment.
    const durationMs = timing.elapsedMs(timingRef.current, Date.now());
    const source: RecordingSource =
      activeSourceRef.current === "both" ? "Combined"
      : activeSourceRef.current === "system" ? "System"
      : "Microphone";
    const prefix =
      source === "Combined" ? t("recTitlePrefixBoth")
      : source === "System" ? t("recTitlePrefixSystem")
      : t("recTitlePrefixMic");
    const title = `${prefix} ${new Date().toLocaleString()}`;
    const rec: PendingRecording = { userId: userId ?? "", blob, title, durationMs, source, createdAt: Date.now() };

    // Stash the audio BEFORE uploading. If the upload fails (e.g. an expired session redirects to login),
    // the recording survives in local storage and is offered for re-upload on the next visit.
    if (userId) await savePendingRecording(rec);

    try {
      const created = await api.upload(blob, title, durationMs, source);
      if (userId) await clearPendingRecording(userId);
      setPending(null);
      // Attach any live notes to the new recording (failure keeps them durable + shows the retry banner).
      await attachNotes(created.id);
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
      const created = await api.upload(pending.blob, pending.title, pending.durationMs, pending.source);
      if (userId) await clearPendingRecording(userId);
      setPending(null);
      // Recovered audio adopts any note lines stashed with it (recordingId null = never attached).
      if (liveLinesRef.current.length === 0 && userId) {
        const stash = await loadPendingNotes(userId);
        if (stash && stash.recordingId === null && stash.lines.length > 0) {
          await attachNotes(created.id, { ...stash, recordingId: created.id });
        }
      } else {
        await attachNotes(created.id);
      }
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
    // Notes about discarded audio die with it.
    if (userId) await clearPendingNotes(userId);
    liveLinesRef.current = [];
    setLiveLines([]);
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
          onFocus={ensureDeviceLabels}
          disabled={recording}
          aria-label={t("sourceMicrophone")}
          className="rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        >
          {buildSourceOptions(
            devices,
            hasLabels,
            {
              micDefault: t("sourceMicDefault"),
              noMic: t("sourceNoMic"),
              numbered: (n) => t("sourceMicNumbered", { n }),
            },
            { canSystemAudio: CAN_SYSTEM_AUDIO },
          ).map((o) => (
            <option key={o.token} value={o.token}>
              {o.label}
            </option>
          ))}
        </select>

        {/* Add system audio to the capture (mixed with the mic, or on its own for "No microphone").
            Shown only where getDisplayMedia can capture it. */}
        {CAN_SYSTEM_AUDIO && (
          <label className="flex items-center gap-1.5 text-sm text-gray-700 dark:text-gray-200">
            <input
              type="checkbox"
              checked={systemAudio}
              onChange={(e) => toggleSystemAudio(e.target.checked)}
              disabled={recording}
            />
            {t("systemAudioToggle")}
          </label>
        )}

        {/* Capture-constraint popover — mic only (no mic to tune when "No microphone" is selected). */}
        <div className="relative" ref={cogRef}>
          <button
            type="button"
            onClick={() => setCogOpen((o) => !o)}
            disabled={recording || selection.kind === "none"}
            title={t("audioSettings")}
            aria-label={t("audioSettings")}
            aria-expanded={cogOpen}
            className="rounded border px-2 py-1 text-sm disabled:opacity-40 dark:border-gray-700 dark:text-gray-100 dark:hover:bg-gray-800"
          >
            ⚙
          </button>
          {cogOpen && selection.kind !== "none" && (
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

        {recording ? (
          <>
            <button
              type="button"
              onClick={paused ? resume : pause}
              className="rounded border px-3 py-1.5 text-sm dark:border-gray-700 dark:text-gray-100 dark:hover:bg-gray-800"
            >
              {paused ? t("recResume") : t("recPause")}
            </button>
            <button onClick={stop} className="rounded bg-red-600 px-3 py-1.5 text-sm text-white">
              {t("recStop")}
            </button>
          </>
        ) : (
          <button
            onClick={() => start()}
            disabled={busy || (selection.kind === "none" && !systemAudio)}
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

        {recording && (
          <>
            {paused ? (
              <span role="status" className="font-mono text-sm text-amber-600">
                ❚❚ {mmss} · {t("recPaused")}
              </span>
            ) : (
              <span className="font-mono text-sm text-red-600">● {mmss}</span>
            )}
            {/* Meter (and its silence detection) only runs while actively capturing. */}
            {!paused && <InputLevelMeter stream={streamRef.current} onSilentChange={setSilent} />}
            {/* Live notes toggle: reopen the panel after it was closed. */}
            {!notesOpen && (
              <button
                type="button"
                onClick={openNotes}
                className="rounded border px-2 py-1 text-xs dark:border-gray-700 dark:text-gray-100 dark:hover:bg-gray-800"
              >
                {t("liveNotesToggle")}
              </button>
            )}
          </>
        )}
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
      {notice && <p className="mt-2 text-xs text-amber-600 dark:text-amber-400">{notice}</p>}
      {labelsTried && !hasLabels && !recording && !error && (
        <p className="mt-2 text-xs text-gray-500 dark:text-gray-400">{t("noMicHint")}</p>
      )}
      {recording && !paused && silent && (
        <p role="status" aria-live="polite" className="mt-2 text-xs text-gray-500 dark:text-gray-400">
          {t("noSoundHint")}
        </p>
      )}
      {/* Live notes panel: floats below the TopBar while recording (incl. paused). */}
      {recording && notesOpen && (
        <LiveNotesPanel
          lines={liveLines}
          onAdd={addLiveNote}
          onEdit={editLiveNote}
          onDelete={deleteLiveNote}
          onClose={closeNotes}
        />
      )}
      {/* Notes attached-failure banner: the audio uploaded, the lines are safe - offer a retry. */}
      {notesAttach && !recording && (
        <div className="mt-2 flex flex-wrap items-center gap-2 rounded border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/30 dark:text-amber-200">
          <span>{t("notesAttachFailed")}</span>
          <button
            type="button"
            onClick={() => void attachNotes(notesAttach.recordingId!, notesAttach)}
            className="ml-auto rounded bg-amber-600 px-2 py-1 text-xs text-white"
          >
            {t("notesAttachRetry")}
          </button>
        </div>
      )}
    </div>
  );
}
