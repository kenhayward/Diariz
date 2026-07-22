import { useCallback, useEffect, useRef, useState, type ChangeEvent, type ReactNode } from "react";
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
import { useStatus } from "../lib/status";
import { useRoom } from "../lib/rooms";
import { RoomPermission } from "../lib/types";
import type { StatusTone } from "../lib/statusBar";
import RecordHero from "./hub/RecordHero";
import AudioSourceChip from "./hub/AudioSourceChip";
import AudioSourcePopover from "./hub/AudioSourcePopover";
import AutoStopPopover from "./hub/AutoStopPopover";
import NotesPopover from "./hub/NotesPopover";
import HubIconButton from "./hub/HubIconButton";
import { useHubPopover } from "./hub/hubPopovers";
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
import * as schedule from "../lib/recorderSchedule";
import type { AutoStopChoice } from "../lib/recorderSchedule";
import {
  savePendingNotes,
  loadPendingNotes,
  clearPendingNotes,
  type PendingNotes,
} from "../lib/pendingNotes";
import { canCaptureScreenshots, onScreenshotCaptured, type CapturedShot } from "../lib/trayScreenshots";
import {
  savePendingScreenshots,
  loadPendingScreenshots,
  clearPendingScreenshots,
  type PendingScreenshots,
  type PendingShot,
} from "../lib/pendingScreenshots";
import type { MeetingNote, RecordingSource } from "../lib/types";

const SOURCE_KEY = "diariz.recorder.source";
const CONSTRAINTS_KEY = "diariz.recorder.audioConstraints";
const SYSTEM_AUDIO_KEY = "diariz.recorder.systemAudio";
const AUTOSTOP_KEY = "diariz.recorder.autoStop";

// Whether this environment can capture system audio at all (Chromium/desktop). Drives the System audio
// checkbox + the "No microphone" dropdown option; false in Firefox/Safari.
const CAN_SYSTEM_AUDIO = supportsDisplayAudio() || isElectron;

// Command-hub icon-button glyphs (Feather/Lucide-style, 18px, drawn in `currentColor` so the button's own
// text colour applies). Auto-stop = clock, Upload = tray/upload-arrow, Notes = pencil. The buttons are
// icon-only: the label lives on aria-label + title. (The record/pause/resume/stop glyphs live in RecordHero.)
function HubIcon({ children }: { children: ReactNode }) {
  return (
    <svg viewBox="0 0 24 24" width="18" height="18" fill="none" aria-hidden="true" focusable="false"
      stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
      {children}
    </svg>
  );
}

const IconClock = () => (
  <HubIcon>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 7.5V12l3 2" />
  </HubIcon>
);

const IconUpload = () => (
  <HubIcon>
    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12" />
  </HubIcon>
);

const IconPencil = () => (
  <HubIcon>
    <path d="M17 3a2.83 2.83 0 0 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z" />
  </HubIcon>
);

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

// The persisted auto-stop preference ({ choice, time }); the resolved target itself is never persisted
// (it's re-derived on selection / at Record time and cleared on stop).
function loadSavedAutoStop(): { choice: AutoStopChoice; time: string } {
  try {
    const raw = localStorage.getItem(AUTOSTOP_KEY);
    if (raw) {
      const parsed = JSON.parse(raw) as { choice?: AutoStopChoice; time?: string };
      return { choice: parsed.choice ?? "off", time: parsed.time ?? "" };
    }
  } catch {
    /* storage unavailable / malformed — fall through to the default */
  }
  return { choice: "off", time: "" };
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
  // Shared "one popover open at a time" state for the top-bar hub. The audio-source popover is id "source";
  // used via a safe fallback when Recorder is rendered outside a HubPopoverProvider (e.g. unit tests).
  const hub = useHubPopover();
  // True once we've asked the browser for mic access to reveal device labels (on first picker focus).
  // Used to show the "no microphone detected" hint only after an attempt that came back empty.
  const [labelsTried, setLabelsTried] = useState(false);
  const [recording, setRecording] = useState(false);
  // Paused mid-recording: capture is suspended (nothing recorded, mic muted) but the recorder is still live.
  const [paused, setPaused] = useState(false);
  // True once the input has been near-silent for a sustained period while recording (see HubLevelMeter).
  const [silent, setSilent] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  // Auto-stop: schedule the current recording to end after N minutes or at a set clock time. The chosen
  // option persists; the resolved absolute target lives in a ref (read by the ticker) mirrored to state
  // (for the "stops at HH:MM" display).
  const [autoStopChoice, setAutoStopChoice] = useState<AutoStopChoice>("off");
  const [autoStopTime, setAutoStopTime] = useState(""); // HH:MM for the "at" option
  const scheduledStopRef = useRef<number | null>(null); // resolved absolute target, read by the ticker
  const [scheduledStopAt, setScheduledStopAt] = useState<number | null>(null); // mirror for display
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
  // Lines whose audio uploaded but whose attach failed (durable, with the recording id) - drives the retry banner.
  const [notesAttach, setNotesAttach] = useState<PendingNotes | null>(null);
  // Screenshots captured while recording: stamped with the *recorded* clock, mirrored to IndexedDB so a
  // crash never loses them, and attached to the recording after upload (exactly like live notes). No
  // popover renders these yet (unlike liveLines), so a ref is enough - there is nothing to re-render for.
  const liveShotsRef = useRef<PendingShot[]>([]);
  // Captures whose audio uploaded but whose attach failed - drives the retry banner.
  const [shotsAttach, setShotsAttach] = useState<PendingScreenshots | null>(null);

  const userId = userIdFromToken(getToken());

  // On mount, surface any unsaved recording stashed for this user - and any note lines / screenshots whose
  // audio uploaded but whose attach failed (they carry the recording id, so the banner can retry).
  useEffect(() => {
    if (!userId) return;
    let cancelled = false;
    void loadPendingRecording(userId).then((rec) => {
      if (!cancelled && rec) setPending(rec);
    });
    void loadPendingNotes(userId).then((stash) => {
      if (!cancelled && stash && stash.lines.length > 0 && stash.recordingId) setNotesAttach(stash);
    });
    void loadPendingScreenshots(userId).then((stash) => {
      if (!cancelled && stash && stash.shots.length > 0 && stash.recordingId) setShotsAttach(stash);
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
  // Mirrors `recording` (true for the whole recording, including while paused) so the screenshot
  // subscription below - mounted once - can tell a live capture from a stray one arriving before Record or
  // after Stop, without resubscribing to the shell on every recording toggle.
  const recordingRef = useRef(false);
  const timerRef = useRef<number | null>(null);
  // A separate wall-clock interval for the auto-stop check. Kept independent of the elapsed ticker (which
  // freezes on pause) so a *paused* recording still auto-stops at its scheduled time.
  const scheduleTimerRef = useRef<number | null>(null);
  // The coarse source actually being recorded (mic vs system); the tray only speaks in these terms,
  // and the upload title/enum needs it, so we can't rely on `selection` state having flushed.
  const activeSourceRef = useRef<AudioSourceKind>("mic");
  // Reports phase changes to the Electron tray; a no-op in a plain browser.
  const reportRef = useRef<(s: RecorderState) => void>(() => {});

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
    const savedAutoStop = loadSavedAutoStop();
    setAutoStopChoice(savedAutoStop.choice);
    setAutoStopTime(savedAutoStop.time);
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
  }

  function toggleSystemAudio(on: boolean) {
    setSystemAudio(on);
    try {
      localStorage.setItem(SYSTEM_AUDIO_KEY, String(on));
    } catch {
      /* non-fatal */
    }
  }

  function persistAutoStop(choice: AutoStopChoice, time: string) {
    try {
      localStorage.setItem(AUTOSTOP_KEY, JSON.stringify({ choice, time }));
    } catch {
      /* non-fatal */
    }
  }

  // Recompute the absolute stop target from the current choice and store it in both the ref (read by the
  // ticker) and state (drives the "stops at HH:MM" display). `anchorMs` is the base for relative choices.
  const applySchedule = useCallback((choice: AutoStopChoice, time: string, anchorMs: number) => {
    const at = schedule.resolveStopAt(choice, time, anchorMs, Date.now());
    scheduledStopRef.current = at;
    setScheduledStopAt(at);
  }, []);

  // On change (persist + re-resolve). Anchor = now when changed here; a relative choice set before Record
  // is re-anchored to record-start inside start().
  function onAutoStopChoice(choice: AutoStopChoice) {
    setAutoStopChoice(choice);
    persistAutoStop(choice, autoStopTime);
    applySchedule(choice, autoStopTime, Date.now());
  }
  function onAutoStopTime(time: string) {
    setAutoStopTime(time);
    persistAutoStop(autoStopChoice, time);
    applySchedule(autoStopChoice, time, Date.now());
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

  // Auto-stop watcher: a wall-clock interval that ends the recording once the scheduled target is reached
  // (which runs the normal upload + transcription). Runs from start() to stop() regardless of pause, so a
  // paused recording still stops on time. `stop()` is a hoisted function declaration, so calling it is safe.
  function startScheduleWatcher() {
    if (scheduleTimerRef.current) window.clearInterval(scheduleTimerRef.current);
    scheduleTimerRef.current = window.setInterval(() => {
      if (schedule.shouldStop(scheduledStopRef.current, Date.now())) stop();
    }, 1000);
  }

  function stopScheduleWatcher() {
    if (scheduleTimerRef.current) window.clearInterval(scheduleTimerRef.current);
    scheduleTimerRef.current = null;
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

  // The notes popover's open state lives in the shared hub (id "notes"); this only persists the *preference*
  // so a fresh recording reopens it (or not) per the user's last choice. Kept in sync with the pencil toggle
  // + the popover's close button below.
  function persistNotesOpen(open: boolean) {
    try {
      localStorage.setItem(NOTES_OPEN_KEY, open ? "true" : "false");
    } catch {
      /* non-fatal */
    }
  }

  // Pencil-button toggle: flip the notes popover and remember the resulting preference.
  function toggleNotes() {
    const willOpen = !hub.isOpen("notes");
    hub.toggle("notes");
    persistNotesOpen(willOpen);
  }

  // The popover's own close (X / backdrop-independent): close it and remember "closed".
  function closeNotes() {
    hub.close();
    persistNotesOpen(false);
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

  // ---- Live screenshots (captured while recording; attached to the recording after upload) ----
  //
  // The Electron shell owns the capture itself (hotkey, tray, capture-area picker) and the re-entrancy /
  // cooldown guard around it (see screenshotState.js's shouldStartCapture) - the renderer never initiates a
  // capture and never dedupes one; it only stamps, stashes and attaches what arrives. This mirrors the live
  // notes handling above exactly.

  /// Update the captures and mirror them to IndexedDB (recordingId null = still recording).
  function mirrorShots(shots: PendingShot[]) {
    liveShotsRef.current = shots;
    if (userId) void savePendingScreenshots({ userId, recordingId: null, updatedAt: Date.now(), shots });
  }

  function addLiveShot(shot: CapturedShot) {
    mirrorShots([
      ...liveShotsRef.current,
      {
        capturedAtMs: timing.elapsedMs(timingRef.current, Date.now()),
        width: shot.width,
        height: shot.height,
        full: shot.full,
        thumb: shot.thumb,
      },
    ]);
  }

  /// Attach captures to the created recording. Success clears the durable stash; failure keeps them (with
  /// the recording id) and surfaces the retry banner. A screenshot failure never fails the upload itself.
  /// Uploads one at a time (unlike notes' single bulk call) because each is a multipart request; on a
  /// failure partway through, only the *un-uploaded remainder* is re-stashed, so a retry can't re-post
  /// captures the server already has.
  async function attachScreenshots(recordingId: string, fromRetry?: PendingScreenshots) {
    const shots = fromRetry ? fromRetry.shots : liveShotsRef.current;
    if (shots.length === 0) {
      if (userId) void clearPendingScreenshots(userId);
      return;
    }
    let uploaded = 0;
    try {
      for (const shot of shots) {
        await api.createScreenshot(recordingId, shot);
        uploaded++;
      }
      if (userId) await clearPendingScreenshots(userId);
      liveShotsRef.current = [];
      setShotsAttach(null);
    } catch {
      const remaining = shots.slice(uploaded);
      const stash: PendingScreenshots = { userId: userId ?? "", recordingId, shots: remaining, updatedAt: Date.now() };
      if (userId) await savePendingScreenshots(stash);
      setShotsAttach(stash);
    }
  }

  // Captures arrive from the Electron shell; the renderer stamps them with the recording clock because it
  // is the only side that knows about pauses. Mounted once (no-op in a plain browser) and reads
  // `recordingRef` rather than depending on `recording`, so a stray hotkey outside an active recording
  // (before the first Record, or after Stop) can never enqueue an orphaned capture.
  useEffect(() => {
    if (!canCaptureScreenshots()) return;
    return onScreenshotCaptured((shot) => {
      if (!recordingRef.current) return;
      addLiveShot(shot);
    });
  }, []);

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

    // Snapshot where this take should be filed at the moment of Record. Into a shared room: share it there,
    // main placement ungrouped. Into the personal room: the placement-preference folder.
    if (currentRoom && !currentRoom.isPersonal) {
      pendingRoomRef.current = currentRoom.id;
      pendingSectionRef.current = null;
    } else {
      pendingRoomRef.current = null;
      pendingSectionRef.current = recordingSectionId;
    }
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
      // Re-anchor a relative auto-stop to record-start, so "in N minutes" means N minutes of recording.
      applySchedule(autoStopChoice, autoStopTime, Date.now());
      setElapsed(0);
      startTicker();
      startScheduleWatcher();
      recordingRef.current = true;
      setRecording(true);
      setPaused(false);
      // Fresh notes for a fresh recording: clear any stale unattached lines (orphans from a crash whose
      // audio never reached Stop - there is nothing to attach them to) and open the panel per preference.
      liveLinesRef.current = [];
      setLiveLines([]);
      if (userId) void clearPendingNotes(userId);
      // Same for screenshots: a previous recording whose audio upload never even started (so attach was
      // never reached) would otherwise leak its captures into this new take.
      liveShotsRef.current = [];
      if (userId) void clearPendingScreenshots(userId);
      // Auto-open the notes popover per the remembered preference. `stop()` resets the hub, so at record
      // start nothing else is open and `toggle` reliably *opens* notes.
      if (localStorage.getItem(NOTES_OPEN_KEY) !== "false" && !hub.isOpen("notes")) hub.toggle("notes");
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
    stopScheduleWatcher();
    // Fold any running segment so the uploaded duration is final and paused-free.
    timingRef.current = timing.pause(timingRef.current, Date.now());
    // Clear the resolved auto-stop target so a finished schedule can't re-fire and the display clears.
    scheduledStopRef.current = null;
    setScheduledStopAt(null);
    recordingRef.current = false;
    setRecording(false);
    setPaused(false);
    setSilent(false);
    // Reset any open hub popover (the notes popover only lives while recording) so the next recording's
    // auto-open toggle starts from a clean "nothing open" state.
    hub.close();
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
      const created = await api.upload(blob, title, durationMs, source, pendingSectionRef.current, pendingRoomRef.current);
      if (userId) await clearPendingRecording(userId);
      setPending(null);
      // Attach any live notes / screenshots to the new recording (failure keeps them durable + shows the
      // retry banner; a screenshot failure never fails the audio upload itself, which already succeeded).
      await attachNotes(created.id);
      await attachScreenshots(created.id);
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
    // Notes and screenshots about discarded audio die with it.
    if (userId) await clearPendingNotes(userId);
    liveLinesRef.current = [];
    setLiveLines([]);
    if (userId) await clearPendingScreenshots(userId);
    liveShotsRef.current = [];
  }

  // Upload existing audio files (the "Upload" button). The shared upload queue handles validation,
  // per-file status, and refreshing the list; you can also drag files onto the recordings panel.
  const uploads = useUpload();
  // Recording (and uploading a file) requires CreateRecording in the current room. Always true in a personal
  // room; the gate becomes real once you can be a low-privilege member of a shared room.
  const { can, recordingSectionId, currentRoom } = useRoom();
  const canRecord = can(RoomPermission.CreateRecording);
  // The folder + room a take should land in, snapshotted when Record is pressed (the user may navigate away
  // before Stop, so we can't read them live at upload time). Recording into a shared room shares it there and
  // keeps the main placement ungrouped in the personal room (so no section then).
  const pendingSectionRef = useRef<string | null>(null);
  const pendingRoomRef = useRef<string | null>(null);
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
  // The "stops at HH:MM" hint (shown inside the Auto-stop popover) once a stop is scheduled.
  const scheduledHint =
    scheduledStopAt != null
      ? t("autoStopScheduled", {
          time: new Date(scheduledStopAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }),
        })
      : null;

  // Errors, warnings and hints go to the app-wide status bar rather than inline: the TopBar is a
  // fixed-height header, so an extra line here pushed the whole bar off screen. One message at a time,
  // most severe first; the tones keep the colours these lines had inline (red / amber / grey).
  const { setStatus } = useStatus();
  const statusText = error ?? notice ?? null;
  const statusTone: StatusTone = error ? "error" : "progress";
  const hint =
    recording && !paused && silent ? t("noSoundHint")
    : labelsTried && !hasLabels && !recording ? t("noMicHint")
    : null;
  const message = statusText ?? hint;
  const tone: StatusTone = statusText ? statusTone : "info";
  // Only clear what we pushed, so we never wipe another component's message.
  const pushedRef = useRef(false);
  useEffect(() => {
    if (message) {
      setStatus(message, tone, { sticky: true });
      pushedRef.current = true;
    } else if (pushedRef.current) {
      setStatus(null);
      pushedRef.current = false;
    }
  }, [message, tone, setStatus]);
  // Clear a lingering message when the recorder unmounts (e.g. sign-out).
  useEffect(
    () => () => {
      if (pushedRef.current) setStatus(null);
    },
    [setStatus],
  );

  return (
    // `relative` anchors the recovery popover below the controls without adding to the bar's height.
    <div
      className={
        compact ? "relative" : "relative rounded-lg border bg-white p-4 dark:border-gray-700 dark:bg-gray-900"
      }
    >
      <div className="flex items-center gap-2">
        {/* Single "Audio source" chip - opens the Audio source popover (mic select + system toggle +
            processing chips). Anchored in a `relative` wrapper so the popover positions under the chip. */}
        <div className="relative">
          <AudioSourceChip
            systemAudio={systemAudio}
            expanded={hub.isOpen("source")}
            disabled={recording}
            onClick={() => hub.toggle("source")}
          />
          <AudioSourcePopover
            open={hub.isOpen("source")}
            onClose={hub.close}
            selection={selection}
            options={buildSourceOptions(
              devices,
              hasLabels,
              {
                micDefault: t("sourceMicDefault"),
                noMic: t("sourceNoMic"),
                numbered: (n) => t("sourceMicNumbered", { n }),
              },
              { canSystemAudio: CAN_SYSTEM_AUDIO },
            )}
            onSelectSource={onSelectSource}
            onFocusSelect={ensureDeviceLabels}
            recording={recording}
            canSystemAudio={CAN_SYSTEM_AUDIO}
            systemAudio={systemAudio}
            onToggleSystemAudio={toggleSystemAudio}
            constraints={constraints}
            onToggleConstraint={toggleConstraint}
          />
        </div>

        <RecordHero
          recording={recording}
          paused={paused}
          mmss={mmss}
          stream={streamRef.current}
          canRecord={canRecord}
          busy={busy}
          startDisabled={selection.kind === "none" && !systemAudio}
          onStart={() => start()}
          onPause={pause}
          onResume={resume}
          onStop={stop}
          onSilentChange={setSilent}
        />

        {/* Auto-stop: clock icon button -> Auto-stop popover. Same choice/time state as the old select. */}
        <div className="relative">
          <HubIconButton
            label={t("autoStopLabel")}
            onClick={() => hub.toggle("stop")}
            disabled={busy || !canRecord}
            expanded={hub.isOpen("stop")}
          >
            <IconClock />
          </HubIconButton>
          <AutoStopPopover
            open={hub.isOpen("stop")}
            onClose={hub.close}
            choice={autoStopChoice}
            time={autoStopTime}
            onChoice={onAutoStopChoice}
            onTime={onAutoStopTime}
            scheduledHint={scheduledHint}
          />
        </div>

        {/* Upload: icon button (restyled) + the unchanged hidden file input. */}
        <HubIconButton
          label={t("recUpload")}
          title={!canRecord ? t("recNoPermission") : t("recUploadTitle")}
          onClick={() => fileRef.current?.click()}
          disabled={recording || busy || !canRecord}
        >
          <IconUpload />
        </HubIconButton>
        <input
          ref={fileRef}
          type="file"
          accept={AUDIO_ACCEPT_ATTR}
          multiple
          onChange={onPickFiles}
          className="hidden"
          data-testid="upload-input"
        />

        {/* Notes: pencil icon button (recording-only) -> Notes popover. */}
        {recording && (
          <div className="relative">
            <HubIconButton
              label={t("liveNotesToggle")}
              onClick={toggleNotes}
              expanded={hub.isOpen("notes")}
            >
              <IconPencil />
            </HubIconButton>
            <NotesPopover
              open={hub.isOpen("notes")}
              onClose={closeNotes}
              lines={liveLines}
              onAdd={addLiveNote}
              onEdit={editLiveNote}
              onDelete={deleteLiveNote}
            />
          </div>
        )}
      </div>

      {/* Recovery banners float below the bar in a popover. They must stay out of the TopBar's flow: it is
          a fixed-height header, so an in-flow banner grows it and pushes the page down. */}
      {!recording && (pending || notesAttach || shotsAttach) && (
        <div
          data-testid="recorder-popover"
          className="absolute left-1/2 top-full z-40 mt-1 w-[28rem] max-w-[calc(100vw-2rem)] -translate-x-1/2 space-y-2"
        >
          {pending && (
            <div className="flex flex-wrap items-center gap-2 rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800 shadow-xl dark:border-amber-700 dark:bg-amber-900/30 dark:text-amber-200">
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
          {/* Notes attached-failure banner: the audio uploaded, the lines are safe - offer a retry. */}
          {notesAttach && (
            <div className="flex flex-wrap items-center gap-2 rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800 shadow-xl dark:border-amber-700 dark:bg-amber-900/30 dark:text-amber-200">
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
          {/* Screenshots attached-failure banner: the audio uploaded, the captures are safe - offer a retry. */}
          {shotsAttach && (
            <div className="flex flex-wrap items-center gap-2 rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800 shadow-xl dark:border-amber-700 dark:bg-amber-900/30 dark:text-amber-200">
              <span>{t("screenshotsAttachFailed")}</span>
              <button
                type="button"
                onClick={() => void attachScreenshots(shotsAttach.recordingId!, shotsAttach)}
                className="ml-auto rounded bg-amber-600 px-2 py-1 text-xs text-white"
              >
                {t("screenshotsAttachRetry")}
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
