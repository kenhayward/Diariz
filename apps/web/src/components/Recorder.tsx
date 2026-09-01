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
import { setCapturing } from "../lib/captureState";
import { useNotesPopout } from "../lib/useNotesPopout";
import type { NotesState } from "../lib/notesChannel";
import { onRecordingRequested, type CalendarEventContext, type RecordingRequest } from "../lib/recordRequest";
import {
  resolveCalendarStopAt,
  earlierStop,
  shouldPromptExtend,
  extendedStopAt,
  RECENT_SOUND_MS,
} from "../lib/calendarRecording";
import { startSilenceWatcher, type SilenceWatcher } from "../lib/silenceWatcher";
import { startLiveSession, type LiveSession } from "../lib/liveSession";
import { indexedDbChunkStore } from "../lib/liveChunkQueue";
import { useLiveTranscript } from "../lib/useLiveTranscript";
import { createHub } from "../lib/signalr";
import { useCalendarRecordingSettings } from "../lib/calendarRecordingSettings";
import { useStatus } from "../lib/status";
import { useElapsedSeconds } from "../lib/elapsedSeconds";
import { useToast } from "../lib/toast";
import { useRoom } from "../lib/rooms";
import { RoomPermission } from "../lib/types";
import type { StatusTone } from "../lib/statusBar";
import RecordHero from "./hub/RecordHero";
import AudioSourceChip from "./hub/AudioSourceChip";
import AudioSourcePopover from "./hub/AudioSourcePopover";
import AutoStopPopover from "./hub/AutoStopPopover";
import NotesPopover from "./hub/NotesPopover";
import HubIconButton from "./hub/HubIconButton";
import MoreControlsPopover from "./hub/MoreControlsPopover";
import { IconCamera, IconClock, IconPencil, IconUpload, IconMore } from "./hub/hubIcons";
import { useHubPopover } from "./hub/hubPopovers";
import { MEDIA_ACCEPT_ATTR } from "../lib/mediaKinds";
import { pickMediaFiles } from "../lib/mediaPicker";
import { retryOnGatewayError } from "../lib/retry";
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
import {
  canCaptureScreenshots,
  hasCaptureArea,
  onCaptureAreaChanged,
  canAutoCapture,
  onAutoCaptureChanged,
  requestToggleAutoCapture,
  onScreenshotCaptured,
  requestCapture,
  requestChangeArea,
  type CapturedShot,
} from "../lib/trayScreenshots";
import { createSlideCapture, type SlideCapture } from "../lib/slideCapture";
import { openDisplayMediaFrames } from "../lib/displayMediaFrames";
import {
  addPendingScreenshot,
  loadPendingScreenshots,
  removePendingScreenshot,
  setPendingScreenshotsRecordingId,
  clearPendingScreenshots,
  type PendingScreenshots,
  type PendingShot,
} from "../lib/pendingScreenshots";
import type { RecordingSource } from "../lib/types";
import { useLiveNotes } from "../lib/useLiveNotes";

const SOURCE_KEY = "diariz.recorder.source";
const CONSTRAINTS_KEY = "diariz.recorder.audioConstraints";
const SYSTEM_AUDIO_KEY = "diariz.recorder.systemAudio";
const AUTOSTOP_KEY = "diariz.recorder.autoStop";

// A hard ceiling on captures kept per recording (see addLiveShot). pendingScreenshots stashes each
// capture as its own IndexedDB record (addPendingScreenshot/removePendingScreenshot), so - unlike the old
// whole-array-per-capture stash - storage *writes* no longer grow with the square of the capture count;
// this cap is no longer a write-churn guard. It still bounds *memory*: every stashed capture's full PNG
// + JPEG thumbnail Blob stays referenced for the meeting's duration (`liveShotsRef`/`liveShots`, needed
// for the live strip and for attach-on-stop), so an unbounded stash would let a marathon meeting - or a
// runaway held hotkey/tray click - pin an unbounded amount of image data in the renderer. At a
// representative ~3 MB per capture (full PNG + JPEG thumb), 200 captures caps that at roughly 600 MB -
// well beyond any realistic meeting's capture count (a capture every couple of minutes for six-plus
// hours), while still bounding the worst case to a fixed ceiling instead of growing forever. Past the
// cap, a capture is dropped (not silently discarded - the user is told) rather than growing without
// bound.
export const MAX_LIVE_SCREENSHOTS = 200;

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

/// Why a recording ended. Absent means the user pressed Stop, which needs no announcement - they know.
type StopReason = "schedule" | "calendar" | "silence";

/// Kept as a literal map rather than a template key, so every key is greppable in the catalogues.
const STOP_TOAST: Record<StopReason, string> = {
  schedule: "recStoppedSchedule",
  calendar: "recStoppedCalendar",
  silence: "recStoppedSilence",
};

/// Raise an OS notification for the extend prompt.
///
/// Works in both a browser and the Electron shell with no main-process involvement: the SPA has no CSP,
/// `apps/desktop` sets no permission request handler (so Electron grants by default), and `setAppUserModelId`
/// is already set on win32, which is what Windows requires for a renderer notification to appear.
///
/// Permission is asked for here, at the first moment it is actually needed, rather than on load - and a
/// refusal degrades silently to the in-app prompt, which is always shown regardless.
async function notify(title: string, body: string) {
  try {
    if (typeof Notification === "undefined") return;
    const permission =
      Notification.permission === "default" ? await Notification.requestPermission() : Notification.permission;
    if (permission === "granted") new Notification(title, { body });
  } catch {
    /* notifications unavailable - the in-app prompt still stands */
  }
}

/// The three recovery banners share one panel style, defined once so they cannot drift apart - they were
/// three copies of the same class string and the alpha background below had to be fixed in all three.
///
/// The background must stay fully opaque. These banners float over the routed page (see the popover block
/// that renders them), so an alpha tint - this was `dark:bg-amber-900/30` - lets that page's own controls
/// read straight through the panel and tangle with the banner's buttons (#601). It is the same reason
/// HubPopover paints on the solid `--hub-popover-bg` token rather than a tint.
const RECOVERY_BANNER =
  "flex flex-wrap items-center gap-2 rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm " +
  "text-amber-800 shadow-xl dark:border-amber-700 dark:bg-amber-950 dark:text-amber-200";

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
  // The calendar event's own end (+ the user's overrun allowance), kept SEPARATE from the user's auto-stop
  // above even though the display shows whichever comes first. They behave differently when they fire: the
  // user's own auto-stop is a hard stop they asked for, while the calendar's only ends a meeting that has
  // actually finished - so the watcher has to be able to tell them apart. Merging them (which is what this
  // used to do) makes that impossible.
  const calendarStopRef = useRef<number | null>(null);
  // The live "your meeting was due to end" question. `deadlineAt` is non-null only when the user has turned
  // the silence rule off, which is the one case where an unanswered prompt has no floor under it.
  const [extendAsk, setExtendAsk] = useState<{ deadlineAt: number | null } | null>(null);
  // Mirrored for the schedule interval, which closes over its first render and would otherwise never see the
  // prompt appear - and would re-fire it every second. `setAsk` is the ONLY writer of either, deliberately:
  // its write is synchronous and does not depend on when React commits, whereas mirroring during render
  // would also be written by a render that is thrown away.
  const extendAskRef = useRef<{ deadlineAt: number | null } | null>(null);
  function setAsk(next: { deadlineAt: number | null } | null) {
    extendAskRef.current = next;
    setExtendAsk(next);
  }
  // How many times the user has said "Extend this meeting" on THIS take. Each extension lasts twice as long as
  // the last, so a meeting that overruns badly is not a stream of prompts (see extendedStopAt).
  const extensionsRef = useRef(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // A non-fatal notice (e.g. we fell back to mic-only because system audio wasn't shared).
  const [notice, setNotice] = useState<string | null>(null);
  // An unsaved recording recovered from local storage (its upload failed previously, e.g. the session
  // expired). Offered back for upload so the audio is never lost.
  const [pending, setPending] = useState<PendingRecording | null>(null);
  // Lines whose audio uploaded but whose attach failed (durable, with the recording id) - drives the retry banner.
  const [notesAttach, setNotesAttach] = useState<PendingNotes | null>(null);
  // Screenshots captured while recording: stamped with the *recorded* clock, stashed to IndexedDB one
  // capture at a time so a crash never loses them, and attached to the recording after upload (exactly
  // like live notes). The notes popover shows a live thumbnail strip of these, so - as useLiveNotes does
  // for note lines - both a ref (read inside upload()/attachScreenshots(), which may run before state has
  // flushed) and state (to re-render the strip) are kept in step by addLiveShot/deleteLiveShot.
  const liveShotsRef = useRef<PendingShot[]>([]);
  const [liveShots, setLiveShots] = useState<PendingShot[]>([]);
  // Captures whose audio uploaded but whose attach failed - drives the retry banner.
  const [shotsAttach, setShotsAttach] = useState<PendingScreenshots | null>(null);
  // Progress through attachScreenshots' sequential per-capture uploads (one multipart POST per shot), so a
  // long meeting's worth of captures shows "n/total" instead of an undifferentiated spinner indistinguishable
  // from the (already-finished) audio upload. Null outside of an attach; drives RecordHero's busy label.
  const [attachProgress, setAttachProgress] = useState<{ done: number; total: number } | null>(null);

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
  // The wall clock this take began, kept deliberately OUTSIDE `timingRef`: timing.pause() folds runningSince
  // into accumulatedMs and nulls it, so by the time upload() runs the original start is gone from Timing. This
  // is what the server matches meetings against, so it has to survive every pause.
  const startedAtRef = useRef<number | null>(null);
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
  // The calendar event this take was started from, when it was. Read inside upload() (which runs from
  // onstop, long after any state would have flushed) to name the recording after the invite.
  const calendarEventRef = useRef<CalendarEventContext | null>(null);
  // Ends the take after N seconds of silence, for a calendar-started recording with that setting on. Null
  // whenever the rule doesn't apply. Owns its own AudioContext so it doesn't depend on the meter being
  // mounted - see silenceWatcher.ts.
  const silenceRef = useRef<SilenceWatcher | null>(null);
  // The live capture for this take, or null when the server could not be reached at start - in which
  // case everything below behaves exactly as it did before chunked upload existed.
  const liveRef = useRef<LiveSession | null>(null);
  // Routes the MediaRecorder's final ondataavailable to finish() rather than to another chunk.
  const liveStoppingRef = useRef(false);
  // How many of chunksRef's fragments the live session has already taken. The tail sent at stop is
  // only what came after the last chunk boundary.
  const liveFragmentsSentRef = useRef(0);
  // The live recording currently being captured, or null. Drives the transcript panel and the
  // hub subscription that feeds it; cleared at stop so neither outlives the take.
  const [liveRecordingId, setLiveRecordingId] = useState<string | null>(null);

  const live = useLiveTranscript(liveRecordingId, () =>
    timing.elapsedMs(timingRef.current, Date.now()));

  // A hub for the duration of the capture only. The recordings list and the detail page have their own
  // connections for their own purposes; this one exists while a meeting is being recorded and goes away
  // with it, so a page that never records never opens it.
  useEffect(() => {
    if (!liveRecordingId) return;
    // Everything here is inside the try: the recording is what the user cannot get back, and the live
    // transcript is a convenience on top of it. A hub that cannot be built or started costs them the
    // live text and nothing else - the audio is still captured, and the full transcript still arrives
    // at the end. Uncaught, the throw would unmount the recorder with the meeting still running.
    let hub: ReturnType<typeof createHub> | null = null;
    try {
      hub = createHub({
        onStatus: () => {},
        onLiveTranscript: (e) => void live.onAppend(e),
        onLiveTranscriptDegraded: live.onDegraded,
      });
      void hub.start().catch(() => {});
    } catch {
      hub = null;
    }
    return () => void hub?.stop().catch(() => {});
  }, [liveRecordingId, live.onAppend, live.onDegraded]);

  /// Feeds the live session's chunk boundary decision. A no-op until a session exists, so it is safe
  /// to hand to a watcher armed before the server answered.
  const feedChunker = useCallback(
    (level: number, dtMs: number, isPaused: boolean) => liveRef.current?.tick(dtMs, level, isPaused),
    [],
  );
  // The in-flight upload of the PREVIOUS take, so a start that replaces a running recording can wait for it.
  // upload() reads pendingRoomRef/pendingSectionRef and the live notes/screenshots AFTER its first await, so
  // starting a new take underneath it would file the finished recording into the new take's folder and steal
  // its notes. Joining a second meeting while the first is recording is exactly that race.
  //
  // The promise is created in stop() rather than in onstop because `MediaRecorder.onstop` is dispatched on a
  // later task: by the time stop() returns there is nothing to await yet, and the replacement would sail past.
  const pendingUploadRef = useRef<Promise<void> | null>(null);
  const uploadDoneRef = useRef<(() => void) | null>(null);
  // Reports phase changes to the Electron tray; a no-op in a plain browser.
  const reportRef = useRef<(s: RecorderState) => void>(() => {});

  /// Every phase change goes through here rather than straight to `reportRef`, so the two things that need
  /// to know about it cannot drift apart. The second is the stale-chunk auto-reload: it tears the page down,
  /// and this recorder lives in it, so it must not fire mid-capture. Uploading counts - the blob is still
  /// only in this page until the request completes.
  function report(state: RecorderState) {
    setCapturing(state.phase === "recording" || state.phase === "uploading");
    reportRef.current(state);
  }

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
    // The display shows whichever target comes first, so changing the user's own auto-stop mid-recording
    // must not wipe the calendar's target off it.
    setScheduledStopAt(earlierStop(at, calendarStopRef.current));
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
      const now = Date.now();
      // The user's own auto-stop is a hard stop: they asked for it, so it is never negotiated.
      if (schedule.shouldStop(scheduledStopRef.current, now)) {
        stop("schedule");
        return;
      }
      // The prompt's own deadline, which only exists when the silence rule is off (see askToExtend).
      const ask = extendAskRef.current;
      if (ask?.deadlineAt != null && now >= ask.deadlineAt) {
        stop("calendar");
        return;
      }
      if (ask) return; // already asking - do not re-fire on every tick
      if (schedule.shouldStop(calendarStopRef.current, now)) askToExtend(now);
    }, 1000);
  }

  /// The calendar's stop time has arrived. If nobody is talking there is nothing to ask about, so the take
  /// ends exactly as it did before; if they are, hold off and ask.
  function askToExtend(nowMs: number) {
    // A paused recording has nobody to ask. `pause()` freezes the silence watcher (the disabled track reads
    // as pure silence), so its last reading is stale - and the floor that makes an unanswered prompt safe is
    // frozen with it, so a prompt raised here would never be answered and the take would never end. The
    // schedule watcher deliberately runs through a pause, and before this feature a paused calendar take
    // stopped and uploaded on time; it still does.
    const paused = recorderRef.current?.state === "paused";
    const silence = paused ? undefined : silenceRef.current?.state();
    // The user's own silence rule IS the window: at this moment either it already considers the meeting over
    // (so there is nobody to ask) or it does not (so ask). Any fixed window would leave a band of quiet where
    // the take is ended silently while the user's own rule still thinks the meeting is running.
    const seconds = calendarSettingsRef.current.silenceSeconds;
    const recentMs = Number.isFinite(seconds) ? seconds * 1000 : RECENT_SOUND_MS;
    if (!silence || !shouldPromptExtend(silence, recentMs)) {
      stop("calendar");
      return;
    }
    // Unanswered, the recording keeps going and the silence rule ends it when the room empties. With silence
    // turned off there is no such floor, so the prompt gets a deadline of its own rather than recording until
    // the browser is closed.
    const deadlineAt =
      seconds > 0
        ? null
        : extendedStopAt(nowMs, calendarSettingsRef.current.afterMinutes, extensionsRef.current);
    setAsk({ deadlineAt });
    void notify(t("extendNotifyTitle"), t("extendPromptText"));
  }

  /// "Extend this meeting": push the calendar's target out by the same overrun allowance the user already
  /// configured, and put the question away. Their own auto-stop is untouched - it still wins if it is sooner.
  function keepRecording() {
    calendarStopRef.current = extendedStopAt(
      Date.now(),
      calendarSettingsRef.current.afterMinutes,
      extensionsRef.current,
    );
    extensionsRef.current += 1;
    setScheduledStopAt(earlierStop(scheduledStopRef.current, calendarStopRef.current));
    setAsk(null);
  }

  function stopScheduleWatcher() {
    if (scheduleTimerRef.current) window.clearInterval(scheduleTimerRef.current);
    scheduleTimerRef.current = null;
  }

  // Clear any running interval on unmount (e.g. the user navigates away mid-recording). Otherwise the
  // elapsed ticker and the auto-stop watcher keep firing on a dead component - and in the test environment
  // they fire after jsdom is torn down, where stop() -> window.clearInterval throws "window is not defined"
  // and vitest fails the run on the unhandled error.
  useEffect(
    () => () => {
      if (timerRef.current) window.clearInterval(timerRef.current);
      if (scheduleTimerRef.current) window.clearInterval(scheduleTimerRef.current);
      // Same reasoning for the silence watcher: its interval and AudioContext would otherwise outlive the
      // component and, in jsdom, fire against a torn-down window.
      silenceRef.current?.stop();
      silenceRef.current = null;
    },
    [],
  );

  // Mute/unmute the live capture tracks. While paused we disable them so nothing is captured *and* the
  // level meter visibly flatlines — a clear "you're not being recorded" signal for sensitive moments.
  function setCaptureEnabled(on: boolean) {
    streamRef.current?.getAudioTracks?.().forEach((tr) => {
      tr.enabled = on;
    });
  }

  // ---- Live notes (taken while recording; attached to the recording after upload) ----

  const NOTES_OPEN_KEY = "diariz.recorder.notesOpen";

  // The lines themselves and their IndexedDB mirror live in useLiveNotes. Attach-on-stop and the retry
  // banner stay here: they reach into upload() and the API, which are this component's business.
  const notes = useLiveNotes({
    userId,
    // The recorded clock, which is pause-aware. The hook never reads a clock of its own.
    stampMs: () => timing.elapsedMs(timingRef.current, Date.now()),
  });

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
      : notes.snapshot().map((l) => ({ text: l.text, capturedAtMs: l.capturedAtMs }));
    if (lines.length === 0) {
      if (userId) void clearPendingNotes(userId);
      return;
    }
    try {
      await api.createNotes(recordingId, lines);
      await notes.reset();
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

  /// Add exactly one new capture: update the ref/state synchronously (the live strip, and attach-on-stop
  /// read the ref before any IndexedDB write could resolve) and stash *only this one record* to
  /// IndexedDB - not a rewrite of the whole growing array (see MAX_LIVE_SCREENSHOTS's comment for why
  /// that used to be an O(n^2) write-churn bug). The id is assigned here (like note lines' ids) so the
  /// stash write can happen fire-and-forget without racing the synchronous state update.
  /// `capturedAtMs` overrides the stamp for a caller that knows better. Auto-capture does: it files the
  /// moment its slide APPEARED, which is several samples before the capture was confirmed, so stamping
  /// on arrival here would place every slide after the sentence that introduced it. A manual capture
  /// omits it - the moment it arrives IS the moment it was taken.
  function addLiveShot(shot: CapturedShot, capturedAtMs?: number) {
    if (liveShotsRef.current.length >= MAX_LIVE_SCREENSHOTS) {
      setNotice(t("screenshotLimitReached", { max: MAX_LIVE_SCREENSHOTS }));
      return;
    }
    const stamped: PendingShot = {
      id: crypto.randomUUID(),
      capturedAtMs: capturedAtMs ?? timing.elapsedMs(timingRef.current, Date.now()),
      width: shot.width,
      height: shot.height,
      full: shot.full,
      thumb: shot.thumb,
    };
    const next = [...liveShotsRef.current, stamped];
    liveShotsRef.current = next;
    setLiveShots(next);
    if (userId) void addPendingScreenshot(userId, stamped);
  }

  /// The per-capture delete button. Filters the *current* ref, not a value captured at render time, so
  /// a rapid string of deletes (or a delete racing an incoming capture) always removes the right item
  /// rather than one computed against a stale array. Removes just that one record from IndexedDB, not
  /// a rewrite of the remaining set.
  ///
  /// Addressed by id rather than position, because the pop-out notes window renders its own copy of
  /// this strip - there the gap between render and click is a whole window boundary wide.
  function deleteLiveShot(id: string) {
    const next = liveShotsRef.current.filter((s) => s.id !== id);
    if (next.length === liveShotsRef.current.length) return;
    liveShotsRef.current = next;
    setLiveShots(next);
    if (userId) void removePendingScreenshot(userId, id);
  }

  /// Attach captures to the created recording. Success clears the durable stash; failure keeps the
  /// un-uploaded remainder (with the recording id) and surfaces the retry banner. A screenshot failure
  /// never fails the upload itself. Uploads one at a time (unlike notes' single bulk call) because each
  /// is a multipart request; each capture is already its own durable IndexedDB record (stashed when
  /// captured, or loaded individually for a retry), so a failure partway through needs no re-stash of the
  /// remainder - only the capture(s) that *did* upload are removed as they go, so what's left in the
  /// store already *is* the un-uploaded remainder, and a retry can't re-post captures the server already
  /// has.
  async function attachScreenshots(recordingId: string, fromRetry?: PendingScreenshots) {
    const shots = fromRetry ? fromRetry.shots : liveShotsRef.current;
    if (shots.length === 0) {
      if (userId) void clearPendingScreenshots(userId);
      return;
    }
    let uploaded = 0;
    // Drives RecordHero's busy label with "n/total" for the length of this call - a long meeting's worth
    // of sequential per-capture POSTs would otherwise sit behind the same undifferentiated "Uploading…"
    // the (already-finished) audio phase used, with no way to tell the attach isn't stuck.
    setAttachProgress({ done: 0, total: shots.length });
    try {
      for (const shot of shots) {
        await api.createScreenshot(recordingId, shot);
        uploaded++;
        setAttachProgress({ done: uploaded, total: shots.length });
        if (userId) await removePendingScreenshot(userId, shot.id);
      }
      if (userId) await clearPendingScreenshots(userId);
      liveShotsRef.current = [];
      setLiveShots([]);
      setShotsAttach(null);
    } catch {
      const remaining = shots.slice(uploaded);
      if (userId) await setPendingScreenshotsRecordingId(userId, recordingId);
      setShotsAttach({ userId: userId ?? "", recordingId, shots: remaining, updatedAt: Date.now() });
    } finally {
      setAttachProgress(null);
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

  // Whether this recording has a capture area yet. Capturing without one opens the shell's area picker and
  // leaves every capture control inert until it settles, so the capture buttons stay disabled until the area
  // is set - making "set the area" the visible first step instead of a button that looks broken. Asked once
  // on mount (a mid-recording reload must not assume "no area") and kept live by the shell's notifications.
  const [captureAreaSet, setCaptureAreaSet] = useState(false);
  useEffect(() => {
    if (!canCaptureScreenshots()) return;
    let active = true;
    void hasCaptureArea().then((has) => {
      if (active) setCaptureAreaSet(has);
    });
    const unsubscribe = onCaptureAreaChanged((has) => setCaptureAreaSet(has));
    return () => {
      active = false;
      unsubscribe();
    };
  }, []);

  // ---- Auto-capture (desktop shell only) ----
  //
  // The shell owns which display is captured and the on/off state; the LOOP runs here, because sampling a
  // held-open getDisplayMedia stream costs ~12ms against ~430ms for a desktopCapturer grab, and because
  // only this side knows the pause-aware recording clock a capture has to be stamped with.

  const [autoCapture, setAutoCapture] = useState(false);
  // The running loop, if any. A ref rather than state: it is torn down from effects and callbacks that
  // must see the current one, never a value captured at render time.
  const slideCaptureRef = useRef<SlideCapture | null>(null);
  // Read inside the loop's tick, which outlives any single render, so both go through refs.
  const pausedRef = useRef(false);
  pausedRef.current = paused;
  const timingForCaptureRef = useRef(timingRef);
  timingForCaptureRef.current = timingRef;

  const stopSlideCapture = useCallback(() => {
    slideCaptureRef.current?.stop();
    slideCaptureRef.current = null;
  }, []);

  useEffect(() => {
    if (!canAutoCapture()) return;

    const unsubscribe = onAutoCaptureChanged((state) => {
      stopSlideCapture();
      setAutoCapture(state.active);
      if (!state.active || !state.area) return;

      void openDisplayMediaFrames(state.area)
        .then((frames) => {
          // The shell can have turned auto-capture off again while the stream was opening (the recording
          // ended, the area changed). Opening one and immediately abandoning it would leave the screen
          // captured with nothing reading it.
          if (!recordingRef.current) return frames.close();
          const capture = createSlideCapture({
            frames,
            nowMs: () => timing.elapsedMs(timingForCaptureRef.current.current, Date.now()),
            isSuspended: () => pausedRef.current,
            maxCaptures: MAX_LIVE_SCREENSHOTS,
            onCapture: (slide) => addLiveShot(slide, slide.capturedAtMs),
            onStopped: (reason) => {
              slideCaptureRef.current = null;
              setNotice(
                reason === "cap-reached"
                  ? t("screenshotAutoCaptureStopped", { max: MAX_LIVE_SCREENSHOTS })
                  : t("screenshotAutoCaptureFailed"),
              );
              // Tell the shell, so the tray checkbox and the in-app toggle stop showing it as running.
              requestToggleAutoCapture();
            },
          });
          slideCaptureRef.current = capture;
          capture.start();
        })
        .catch(() => {
          setNotice(t("screenshotAutoCaptureFailed"));
          requestToggleAutoCapture();
        });
    });

    return () => {
      unsubscribe();
      stopSlideCapture();
    };
  }, []);

  // ---- Pop-out notes window (desktop shell only) ----

  const shellBridge = (window as unknown as { diariz?: TrayBridge }).diariz;

  // Rebuilt on every render the pop-out cares about; useNotesPopout republishes when it changes.
  const notesState: NotesState = {
    lines: notes.lines,
    // Only what the pop-out renders. The full-resolution PNG stays in this window.
    shots: liveShots.map((s) => ({ id: s.id, capturedAtMs: s.capturedAtMs, thumb: s.thumb })),
    canCapture: canCaptureScreenshots(),
    captureAreaSet,
    autoCapture,
    canAutoCapture: canAutoCapture(),
    recording,
  };

  const { poppedOut, popOut, notifyClosed } = useNotesPopout({
    state: notesState,
    openWindow: () => void shellBridge?.openNotesPopout?.(),
    handlers: {
      onAdd: notes.add,
      onEdit: notes.edit,
      onDelete: notes.remove,
      onDeleteShot: deleteLiveShot,
      onCapture: requestCapture,
      onChangeArea: requestChangeArea,
      onToggleAutoCapture: requestToggleAutoCapture,
    },
  });

  // Popping out moves the notes into the window, so the inline popover closes. The remembered
  // preference is deliberately not touched: it records whether the popover auto-opens on the *next*
  // recording, which is a different question from where the notes are right now.
  useEffect(() => {
    if (poppedOut && hub.isOpen("notes")) hub.close();
  }, [poppedOut]);

  // The shell's report is the guaranteed one - it survives the pop-out's renderer being killed, which
  // the channel's own "closing" message does not. That message is merely faster.
  useEffect(() => {
    if (!poppedOut || !shellBridge?.onNotesPopoutClosed) return;
    return shellBridge.onNotesPopoutClosed(() => {
      hub.close();
      notifyClosed();
    });
  }, [poppedOut, notifyClosed]);

  // `trayKind` is set only when the Electron tray drives us (it speaks coarse mic/system); the on-screen
  // button passes nothing and records the current `selection`. A tray "mic" maps to the current specific
  // mic (or default), "system" to loopback.
  async function start(trayKind?: AudioSourceKind, calendarEvent?: CalendarEventContext | null) {
    // Replacing a recording that is already running (you joined a second meeting while the first was still
    // being recorded). End the first properly - it uploads and transcribes on its own, exactly as if you had
    // pressed Stop - and wait for that upload to settle before touching any of the refs it reads.
    if (recordingRef.current) stop();
    if (pendingUploadRef.current) await pendingUploadRef.current;

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
      recorder.ondataavailable = (e) => {
        if (e.data.size === 0) return;
        // Always buffer locally: this is what the fallback upload sends, and what the crash stash
        // holds. Live capture is additive - it never becomes the only copy.
        chunksRef.current.push(e.data);
        const live = liveRef.current;
        if (!live || liveStoppingRef.current) return;
        liveFragmentsSentRef.current = chunksRef.current.length;
        void live.offerFragment(e.data, timing.elapsedMs(timingRef.current, Date.now()));
      };
      recorder.onstop = () => {
        sessionRef.current?.stop();
        sessionRef.current = null;
        streamRef.current = null;
        // Settle the promise stop() handed out, whether the upload succeeded or failed - a replacement take
        // must not be blocked forever by a failed one (the audio is stashed for recovery either way).
        void upload().finally(() => {
          uploadDoneRef.current?.();
          uploadDoneRef.current = null;
          pendingUploadRef.current = null;
        });
      };
      recorder.start();
      // This take's wall clock, read once here and shared by everything that reports it. It is stamped
      // at the top of the take rather than further down because beginLive is fired from this same
      // synchronous block: when the ref was assigned below instead, beginLive read it BEFORE the write
      // and got the PREVIOUS take's start, so every live take after the first in a page session was
      // stamped hours early (#709). The recorder is mounted above the routed content and survives every
      // navigation, so "the previous take" can be from much earlier in the day.
      const startedAt = Date.now();
      startedAtRef.current = startedAt;
      recorderRef.current = recorder;
      liveRef.current = null;
      liveStoppingRef.current = false;
      liveFragmentsSentRef.current = 0;

      // Begin the server-side recording. Deliberately NOT awaited into the start path: the capture is
      // already running, and making the user wait on a round trip to press Record would be a
      // regression. If it fails, liveRef stays null and this take behaves exactly as it did before
      // chunked upload existed - buffered locally, uploaded at stop.
      const liveSource: RecordingSource =
        coarse === "both" ? "Combined" : coarse === "system" ? "System" : "Microphone";
      const liveTitle = calendarEvent?.summary?.trim() || `${t("recTitlePrefixMic")} ${new Date().toLocaleString()}`;
      void startLiveSession({
        begin: () =>
          api.beginLive({
            title: liveTitle,
            source: liveSource,
            sessionId: crypto.randomUUID(),
            // Only used to charge a provisional quota estimate; the real size is reconciled at
            // finalise. An hour is a reasonable guess for a meeting nobody has told us about.
            expectedDurationMs: 60 * 60 * 1000,
            sectionId: pendingSectionRef.current,
            roomId: pendingRoomRef.current,
            // The local, not the ref: this take's start cannot be read out of a ref that a later
            // take will overwrite, and one clock read keeps the live stamp and the fallback
            // upload's stamp identical rather than a few milliseconds apart.
            startedAt,
          }),
        upload: (recordingId, sessionId, chunk) =>
          api.putChunk(recordingId, chunk.sequence, chunk.blob, sessionId, chunk.startMs, chunk.endMs),
        finalize: (recordingId) => api.finalizeLive(recordingId),
        requestFragment: () => {
          try {
            recorderRef.current?.requestData();
          } catch {
            // A recorder that has already stopped. The tail is flushed by stop() anyway.
          }
        },
        store: indexedDbChunkStore,
        onTrouble: (message) => setNotice(message),
      }).then((live) => {
        // A take that was stopped while begin was in flight must not adopt a session nobody will
        // finish - the reaper would collect it, but the user would see a stray recording first.
        if (!live) return;
        if (!recordingRef.current) {
          // Stopped while begin was in flight. The reaper would collect it eventually, but the user
          // would see a stray recording first.
          void api.discardLive(live.recordingId).catch(() => {});
          return;
        }
        liveRef.current = live;
        setLiveRecordingId(live.recordingId);
        // Chunk boundaries need level readings, and only now do we know they are wanted - a take with
        // no live session must not pay for an analyser it never reads. A calendar take already has a
        // watcher (armed above, and already feeding this same callback), so reuse it rather than
        // opening a second AudioContext on the same stream.
        //
        // Deliberately this watcher rather than the on-screen meter: the meter runs on rAF, which
        // stalls in a backgrounded tab, and a chunker that stopped whenever the tab lost focus would
        // stop for most of a meeting. A 0 threshold observes the room without ever auto-stopping.
        if (!silenceRef.current) {
          silenceRef.current = startSilenceWatcher(session.stream, 0, () => stop("silence"), feedChunker);
        }
      });
      activeSourceRef.current = coarse;
      // Always assign, so a plain Record-button take can never inherit the last meeting's name - and for the
      // same reason, always clear the meeting's stop target and its extension count.
      calendarEventRef.current = calendarEvent ?? null;
      calendarStopRef.current = null;
      extensionsRef.current = 0;
      timingRef.current = timing.start(Date.now());
      // Re-anchor a relative auto-stop to record-start, so "in N minutes" means N minutes of recording.
      applySchedule(autoStopChoice, autoStopTime, Date.now());
      // Started from a calendar event: the meeting's own end time is a second, independent answer to "when
      // does this finish". Whichever comes first wins, so the user's own auto-stop choice is never overridden
      // into running longer than they asked.
      if (calendarEvent) {
        const calendarStop = resolveCalendarStopAt(calendarEvent.endsAt, calendarSettingsRef.current, Date.now());
        calendarStopRef.current = calendarStop;
        // The display still shows whichever target comes first, so nothing changes on screen.
        setScheduledStopAt(earlierStop(scheduledStopRef.current, calendarStop));
        // The other end condition: the meeting broke up early and nobody is talking any more.
        if (calendarSettingsRef.current.enabled) {
          silenceRef.current = startSilenceWatcher(
            session.stream,
            calendarSettingsRef.current.silenceSeconds * 1000,
            () => stop("silence"),
            feedChunker,
          );
        }
      }
      setElapsed(0);
      startTicker();
      startScheduleWatcher();
      recordingRef.current = true;
      setRecording(true);
      setPaused(false);
      // Fresh notes for a fresh recording: clear any stale unattached lines (orphans from a crash whose
      // audio never reached Stop - there is nothing to attach them to) and open the panel per preference.
      void notes.reset();
      // Same for screenshots: a previous recording whose audio upload never even started (so attach was
      // never reached) would otherwise leak its captures into this new take.
      liveShotsRef.current = [];
      setLiveShots([]);
      if (userId) void clearPendingScreenshots(userId);
      // Auto-open the notes popover per the remembered preference. `stop()` resets the hub, so at record
      // start nothing else is open and `toggle` reliably *opens* notes.
      if (localStorage.getItem(NOTES_OPEN_KEY) !== "false" && !hub.isOpen("notes")) hub.toggle("notes");
      report({ phase: "recording", source: coarse });
      // A mic grant unlocks device labels — re-enumerate so specifics appear next time.
      if (coarse !== "system") void refreshDevices();
    } catch (e) {
      // Log the raw cause (DOMException name/message) so the actual failure is diagnosable.
      console.error("Audio capture failed:", e);
      const message = describeAudioError(e, coarse, isElectron);
      setError(message);
      report({ phase: "error", error: message });
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
    // Pausing disables the capture track, so the analyser reads pure silence. Without this, pausing for a
    // break would look exactly like the meeting breaking up and end the recording.
    silenceRef.current?.setPaused(true);
    setPaused(true);
  }

  function resume() {
    const rec = recorderRef.current;
    if (!rec || rec.state !== "paused") return;
    setCaptureEnabled(true);
    rec.resume();
    timingRef.current = timing.resume(timingRef.current, Date.now());
    startTicker();
    silenceRef.current?.setPaused(false);
    setPaused(false);
  }

  function stop(reason?: StopReason) {
    stopTicker();
    stopScheduleWatcher();
    // Fold any running segment so the uploaded duration is final and paused-free.
    timingRef.current = timing.pause(timingRef.current, Date.now());
    // Clear the resolved auto-stop targets so a finished schedule can't re-fire and the display clears - both
    // of them, and the pending extend question with them, so the next take can never inherit any of it.
    scheduledStopRef.current = null;
    calendarStopRef.current = null;
    setScheduledStopAt(null);
    setAsk(null);
    recordingRef.current = false;
    setRecording(false);
    setPaused(false);
    setSilent(false);
    // Reset any open hub popover (the notes popover only lives while recording) so the next recording's
    // auto-open toggle starts from a clean "nothing open" state.
    hub.close();
    silenceRef.current?.stop();
    silenceRef.current = null;
    // Publish the "this take is finished with" promise BEFORE asking the recorder to stop: onstop lands on a
    // later task, so a replacement start() that ran in between would otherwise find nothing to wait for.
    if (recorderRef.current) {
      pendingUploadRef.current = new Promise<void>((resolve) => {
        uploadDoneRef.current = resolve;
      });
    }
    recorderRef.current?.stop();
    // An automatic ending is the only one worth announcing: the user did not do it and would otherwise find
    // the recorder idle with no explanation. A replacing start() passes no reason either - that is a handover,
    // and the new recording is its own feedback.
    if (reason) showToast(t(STOP_TOAST[reason]));
  }

  /// Finish a live capture: hand the server the tail and ask it to concatenate. Returns false when
  /// there was no live session, or when finalising failed - in both cases the caller falls back to
  /// uploading the whole blob, which it has buffered all along.
  async function finishLive(): Promise<boolean> {
    const live = liveRef.current;
    if (!live) return false;
    liveRef.current = null;
    setLiveRecordingId(null);
    try {
      const tail = new Blob(chunksRef.current.slice(liveFragmentsSentRef.current), { type: "audio/webm" });
      await live.finish(timing.elapsedMs(timingRef.current, Date.now()), tail);
      // Anything still queued means the server does not have the whole recording, so the concatenated
      // audio would have holes. Fall back rather than ship a damaged take.
      return (await live.pending()).length === 0;
    } catch {
      return false;
    }
  }

  async function upload() {
    setBusy(true);
    report({ phase: "uploading" });
    if (await finishLive()) {
      // The server already has the audio and has queued the transcription. Nothing to upload.
      if (userId) await clearPendingRecording(userId);
      chunksRef.current = [];
      setBusy(false);
      report({ phase: "idle" });
      onUploaded?.();
      return;
    }
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
    // Started from a calendar event: the invite's subject names the recording, so the library reads as the
    // meetings you attended rather than "Microphone recording 09/08/2026, 14:32".
    const calendarEvent = calendarEventRef.current;
    const inviteName = calendarEvent?.summary?.trim() || null;
    const title = inviteName ?? `${prefix} ${new Date().toLocaleString()}`;
    // The wall-clock span. endedAt is "now" because upload() runs from onstop; it is sent separately from
    // durationMs because durationMs excludes paused time, so it cannot reconstruct when the take actually ended.
    const startedAt = startedAtRef.current ?? undefined;
    const endedAt = Date.now();
    const rec: PendingRecording = {
      userId: userId ?? "", blob, title, durationMs, source, createdAt: Date.now(), startedAt, endedAt,
    };

    // Stash the audio BEFORE uploading. If the upload fails (e.g. an expired session redirects to login),
    // the recording survives in local storage and is offered for re-upload on the next visit.
    if (userId) await savePendingRecording(rec);

    try {
      // Retried past a proxy-level gateway error, so an API redeploy during a meeting is not felt as a
      // failed upload. Only 502/503/504 are retried - see retry.ts for why a bare network error is not.
      const created = await retryOnGatewayError(() =>
        api.upload(blob, title, durationMs, source, pendingSectionRef.current, pendingRoomRef.current, {
          startedAt,
          endedAt,
        }),
      );
      if (userId) await clearPendingRecording(userId);
      setPending(null);
      // Pin the invite's subject as the recording's NAME, not just its title. The summariser auto-names a
      // recording whose Name is blank, so leaving it unset would have the model rename the meeting away from
      // what the invite called it. Guarded like the attachments below: the audio is already safely uploaded,
      // and a failed rename must never be reported as a failed recording (it just keeps the title).
      if (inviteName) {
        try {
          await api.renameRecording(created.id, inviteName);
        } catch (e) {
          console.error("Naming the recording after the calendar event failed:", e);
        }
      }
      // Link it to the meeting it was recorded from. Everywhere else this link is *inferred*, by picking the
      // best time-overlapping event when the recording is first opened; here the event id is known for
      // certain, so the link is exact and lands immediately rather than on first view. Marked `manual` for the
      // same reason: this is the user's own choice of meeting, and the auto-matcher must never replace it with
      // an adjacent one - a take started on Join very often overlaps the meeting either side of it. Linking
      // also adopts any prep notes written on the event. Guarded like the rename: the audio is already safely
      // uploaded, and a missing calendar connection or a since-deleted event must never read as a lost
      // recording (the meeting can still be linked by hand afterwards).
      if (calendarEvent) {
        try {
          await api.putCalendarLink(created.id, calendarEvent.id, true, calendarEvent.calendarId);
        } catch (e) {
          console.error("Linking the recording to its calendar event failed:", e);
        }
      }
      // Attach any live notes / screenshots to the new recording (failure keeps them durable + shows the
      // retry banner; a screenshot failure never fails the audio upload itself, which already succeeded).
      // Each is guarded independently, in its own try/catch, rather than sharing this function's outer
      // catch: attachNotes/attachScreenshots already swallow their *own* API failures, but an unexpected
      // exception underneath them (e.g. a storage-layer hiccup while recording the retry stash) must
      // still never be mistaken for the audio upload itself having failed - it already succeeded, so
      // re-offering it below as an unsaved recording would invite a duplicate upload.
      try {
        await attachNotes(created.id);
      } catch (e) {
        console.error("Attaching notes failed unexpectedly:", e);
      }
      try {
        await attachScreenshots(created.id);
      } catch (e) {
        console.error("Attaching screenshots failed unexpectedly:", e);
      }
      onUploaded();
      report({ phase: "idle" });
    } catch (e) {
      const message = apiErrorMessage(e, t("errUpload"));
      setError(message);
      if (userId) setPending(rec); // safe in storage — show the recovery banner
      report({ phase: "error", error: message });
    } finally {
      setBusy(false);
    }
  }

  async function uploadPending() {
    if (!pending) return;
    setBusy(true);
    setError(null);
    try {
      const created = await retryOnGatewayError(() =>
        // Replay the stashed wall clock, not "now": a recording recovered on the next visit still happened when
        // it happened, and stamping it with the recovery moment would lose its meeting.
        api.upload(pending.blob, pending.title, pending.durationMs, pending.source, null, null, {
          startedAt: pending.startedAt,
          endedAt: pending.endedAt,
        }),
      );
      if (userId) await clearPendingRecording(userId);
      setPending(null);
      // Recovered audio adopts any note lines stashed with it (recordingId null = never attached). Milder
      // than upload()'s case (setPending(null) has already run, so a failure here can only produce a false
      // error banner, not a duplicate-offer) but guarded the same way: an unexpected exception in this
      // recovery/attach path must never be blamed on the audio upload, which already succeeded.
      try {
        if (notes.snapshot().length === 0 && userId) {
          const stash = await loadPendingNotes(userId);
          if (stash && stash.recordingId === null && stash.lines.length > 0) {
            await attachNotes(created.id, { ...stash, recordingId: created.id });
          }
        } else {
          await attachNotes(created.id);
        }
      } catch (e) {
        console.error("Attaching notes failed unexpectedly:", e);
      }
      // Same adoption for screenshots stashed with the failed take (recordingId null = never attached) -
      // otherwise they'd stay orphaned in IndexedDB and the next start() would discard them outright.
      try {
        if (liveShotsRef.current.length === 0 && userId) {
          const shotStash = await loadPendingScreenshots(userId);
          if (shotStash && shotStash.recordingId === null && shotStash.shots.length > 0) {
            await attachScreenshots(created.id, { ...shotStash, recordingId: created.id });
          }
        } else {
          await attachScreenshots(created.id);
        }
      } catch (e) {
        console.error("Attaching screenshots failed unexpectedly:", e);
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
    await notes.reset();
    if (userId) await clearPendingScreenshots(userId);
    liveShotsRef.current = [];
    setLiveShots([]);
  }

  // Upload existing audio files (the "Upload" button). The shared upload queue handles validation,
  // per-file status, and refreshing the list; you can also drag files onto the recordings panel.
  const uploads = useUpload();
  // Recording (and uploading a file) requires CreateRecording in the current room. Always true in a personal
  // room; the gate becomes real once you can be a low-privilege member of a shared room.
  const { can, recordingSectionId, currentRoom } = useRoom();
  // Held in a ref as well: start() may run from a tray command or a cross-page request, where the latest
  // render's value is what matters rather than whatever was captured when the handler was subscribed.
  const calendarSettings = useCalendarRecordingSettings();
  const calendarSettingsRef = useRef(calendarSettings);
  calendarSettingsRef.current = calendarSettings;
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

  // Prefer the named picker so the dialog's filter reads "Audio and video files" instead of Chromium's
  // "Custom Files" - `<input accept>` gives no way to label it. Unsupported (Firefox/Safari) or refused
  // returns null, and we open the ordinary input exactly as before.
  async function openFileDialog() {
    const files = await pickMediaFiles();
    if (files === null) {
      fileRef.current?.click();
      return;
    }
    if (files.length) uploads.uploadFiles(files);
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

  // Somewhere else in the app asking us to record - today, the Join-the-meeting button on a calendar event.
  // No audio source is passed, so it uses whatever the user has already chosen on screen rather than
  // second-guessing them from a different page; the request carries only the meeting's own details.
  useEffect(
    () => onRecordingRequested((request: RecordingRequest) => void startFn.current(undefined, request.calendarEvent)),
    [],
  );

  const secs = Math.floor(elapsed / 1000);
  const mmss = `${String(Math.floor(secs / 60)).padStart(2, "0")}:${String(secs % 60).padStart(2, "0")}`;
  // The "stops at HH:MM" hint (shown inside the Auto-stop popover) once a stop is scheduled.
  const scheduledHint =
    scheduledStopAt != null
      ? t("autoStopScheduled", {
          time: new Date(scheduledStopAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }),
        })
      : null;

  // Errors, warnings and hints go to the app-wide status bar rather than inline: the capture bar is a
  // fixed-height header, so an extra line here pushed the whole bar off screen. One message at a time,
  // most severe first; the tones keep the colours these lines had inline (red / amber / grey).
  const { setStatus } = useStatus();
  const { showToast } = useToast();
  // The upload window. Pressing Stop disables the controls and then nothing happens on screen for several
  // seconds on a long recording, which reads as a freeze - so the bar carries a count-up for as long as
  // `busy` holds (which spans the audio POST, the notes/screenshot attaches after it, and the "Upload now"
  // recovery of a stashed recording). It sits above `notice` - a record-start fallback notice is stale by
  // the time you stop. `error` is kept above it defensively rather than behaviourally: every setError sits
  // in the same batch as the setBusy(false) beside it, and start() clears the previous error, so no render
  // has ever seen both - if that changes, the failure must still win. Once captures start posting it keeps
  // the attach's own wording, since that phase has real progress of its own to report.
  const uploadSeconds = useElapsedSeconds(busy);
  const uploadText =
    !busy ? null
    : attachProgress ?
      t("recAttachingScreenshotsElapsed", {
        done: attachProgress.done,
        total: attachProgress.total,
        seconds: uploadSeconds,
      })
    : t("recUploadingElapsed", { seconds: uploadSeconds });
  const statusText = error ?? uploadText ?? notice ?? null;
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

  // Two container-query tiers below the label one (`@xl`, on the chip and the hero). The capture bar is the
  // `@container`, so these ask it - not the window - how much room there is: the bar spans
  // `window - left panel - chat panel`, and a viewport breakpoint measures the wrong box. Each has two
  // thresholds because the recording cluster is ~290px wider than the idle one and so stops fitting much
  // sooner. The strings are written out in full rather than composed: Tailwind generates utilities by
  // scanning source for complete class names and would never see a concatenated variant. The numbers come
  // from measuring the real cluster in a browser - see
  // docs/superpowers/specs/2026-08-20-capture-bar-cluster-collapse-design.md for the widths behind them.
  const clusterGap = recording ? "gap-2 @max-[690px]:gap-1.5" : "gap-2 @max-[400px]:gap-1.5";
  const hideWhenCramped = recording ? "@max-[440px]:hidden" : "@max-[240px]:hidden";
  const showWhenCramped = recording ? "hidden @max-[440px]:block" : "hidden @max-[240px]:block";
  // Upload sheds a tier early while recording: it is disabled for the whole of a recording, so it is the
  // one control that costs its 44px and buys nothing in that state.
  const hideUpload = recording ? "@max-[690px]:hidden" : "@max-[240px]:hidden";
  // What the overflow menu's rows say when they are inert. The inline buttons carry the same gates; the
  // menu stands in for them, so a control unavailable in the bar must be unavailable in the menu too.
  const unavailableReason = !canRecord ? t("recNoPermission") : busy ? t("recUploading") : undefined;

  return (
    // `relative` anchors the recovery popover below the controls without adding to the bar's height.
    <div
      className={
        compact ? "relative" : "relative rounded-lg border bg-white p-4 dark:border-gray-700 dark:bg-gray-900"
      }
    >
      <div className={`flex items-center ${clusterGap}`}>
        {/* Single "Audio source" chip - opens the Audio source popover (mic select + system toggle +
            processing chips). Anchored in a `relative` wrapper so the popover positions under the chip. */}
        <div className="relative">
          <AudioSourceChip
            systemAudio={systemAudio}
            expanded={hub.isOpen("source")}
            disabled={recording}
            recording={recording}
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
          busyLabel={
            attachProgress
              ? t("recAttachingScreenshots", { done: attachProgress.done, total: attachProgress.total })
              : undefined
          }
          startDisabled={selection.kind === "none" && !systemAudio}
          onStart={() => start()}
          onPause={pause}
          onResume={resume}
          // Wrapped rather than passed bare: onStop now takes an optional StopReason, and passing the
          // function reference directly would let RecordHero's onClick hand it the click event as that
          // argument. A user-pressed stop is deliberately reason-less - they know why it stopped.
          onStop={() => stop()}
          onSilentChange={setSilent}
        />

        {/* Auto-stop: clock icon button -> Auto-stop popover. Same choice/time state as the old select.
            Only the BUTTON is hidden when the bar is cramped, never this `relative` wrapper: the wrapper is
            what the popover is positioned against, and the overflow menu still opens it. */}
        <div className="relative">
          <div className={hideWhenCramped}>
            <HubIconButton
              label={t("autoStopLabel")}
              onClick={() => hub.toggle("stop")}
              disabled={busy || !canRecord}
              expanded={hub.isOpen("stop")}
            >
              <IconClock />
            </HubIconButton>
          </div>
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

        {/* Upload: icon button (restyled) + the unchanged hidden file input. The input stays outside the
            hiding wrapper - it is the file dialog the overflow menu's Upload row opens. */}
        <div className={hideUpload}>
          <HubIconButton
            label={t("recUpload")}
            title={!canRecord ? t("recNoPermission") : t("recUploadTitle")}
            onClick={() => void openFileDialog()}
            disabled={recording || busy || !canRecord}
          >
            <IconUpload />
          </HubIconButton>
        </div>
        <input
          ref={fileRef}
          type="file"
          accept={MEDIA_ACCEPT_ATTR}
          multiple
          onChange={onPickFiles}
          className="hidden"
          data-testid="upload-input"
        />

        {/* Screenshot capture: camera icon button (desktop shell + recording only). The in-app equivalent
            of the global hotkey and tray menu item - all three funnel into the same shell capture. Hidden
            while the notes popover is open - it offers its own capture button in that state, and showing
            both at once would just be two identically-labelled controls doing the same thing. */}
        {recording && canCaptureScreenshots() && !hub.isOpen("notes") && (
          <div className={hideWhenCramped}>
          <HubIconButton
            label={t("screenshotCaptureButton")}
            title={t("screenshotCaptureButtonHint")}
            onClick={requestCapture}
            // Same gate as the popover's capture button: with no area chosen this would open the picker and
            // then sit inert until it settled. The area is set from the notes popover (or the tray) - which
            // the button has to be able to say, so it goes inert-but-hoverable rather than `disabled`
            // (a disabled button never renders its tooltip, leaving an icon-only control unexplained).
            disabledReason={captureAreaSet ? undefined : t("screenshotCaptureNeedsArea")}
          >
            <IconCamera />
          </HubIconButton>
          </div>
        )}

        {/* Notes: pencil icon button (recording-only) -> Notes popover. Same rule as auto-stop: the button
            hides when cramped, the `relative` wrapper that hosts the popover does not. */}
        {recording && (
          <div className="relative">
            <div className={hideWhenCramped}>
              <HubIconButton
                label={t("liveNotesToggle")}
                onClick={toggleNotes}
                expanded={hub.isOpen("notes")}
              >
                <IconPencil />
              </HubIconButton>
            </div>
            <NotesPopover
              open={hub.isOpen("notes")}
              onClose={closeNotes}
              lines={notes.lines}
              onAdd={notes.add}
              onEdit={notes.edit}
              onDelete={notes.remove}
              shots={liveShots}
              onDeleteShot={deleteLiveShot}
              liveTranscript={live.transcript ?? undefined}
              liveLagSeconds={live.lagSeconds}
              liveDegraded={live.degraded}
              onChangeCaptureArea={canCaptureScreenshots() ? requestChangeArea : undefined}
              onCapture={canCaptureScreenshots() ? requestCapture : undefined}
              captureAreaSet={captureAreaSet}
              autoCapture={autoCapture}
              onToggleAutoCapture={canAutoCapture() ? requestToggleAutoCapture : undefined}
              // Only the shell can pin a window above a full-screen call, so a plain browser gets no
              // control at all rather than one that opens a tab it cannot float.
              onPopOut={shellBridge?.openNotesPopout ? popOut : undefined}
            />
          </div>
        )}

        {/* The overflow menu, hidden until the bar is too narrow to hold the buttons above - at which point
            it stands in for them, so nothing the bar offers becomes unreachable however narrow the window
            gets. Choosing Auto-stop or Notes does not nest a popover: those panels are absolute children of
            their own `relative` wrappers in this row, so they drop from the bar wherever they were opened
            from, and toggling their id closes this menu on the way (one open popover at a time). */}
        <div className="relative">
          <div className={showWhenCramped}>
            <HubIconButton
              label={t("moreControls")}
              onClick={() => hub.toggle("more")}
              expanded={hub.isOpen("more")}
            >
              <IconMore />
            </HubIconButton>
          </div>
          <MoreControlsPopover
            open={hub.isOpen("more")}
            onClose={hub.close}
            onAutoStop={() => hub.toggle("stop")}
            autoStopDisabledReason={unavailableReason}
            // Absent while recording, where upload is disabled anyway. The two rows that act rather than
            // open a popover close the menu themselves - nothing else would.
            onUpload={
              recording
                ? undefined
                : () => {
                    hub.close();
                    void openFileDialog();
                  }
            }
            uploadDisabledReason={unavailableReason}
            onCapture={
              recording && canCaptureScreenshots()
                ? () => {
                    hub.close();
                    requestCapture();
                  }
                : undefined
            }
            captureDisabledReason={captureAreaSet ? undefined : t("screenshotCaptureNeedsArea")}
            onNotes={recording ? toggleNotes : undefined}
          />
        </div>
      </div>

      {/* The meeting overran. Floated below the bar for the same reason the recovery banners are: the capture
          bar is fixed height, so an in-flow panel grows it and pushes the page down. Deliberately its own
          absolutely-positioned block rather than a fourth banner inside the one below: that block is gated on
          `!recording` and this only ever shows *while* recording, so the two can never collide. */}
      {extendAsk && (
        <div
          data-testid="extend-prompt"
          className="absolute left-1/2 top-full z-40 mt-1 w-[28rem] max-w-[calc(100vw-2rem)] -translate-x-1/2"
        >
          {/* The panel must be fully opaque. It floats over the routed page, so an alpha tint (this was
              `dark:bg-blue-900/30`) lets the page's own header buttons read straight through it and tangle
              with Extend/Stop below - the same reason HubPopover paints on the solid `--hub-popover-bg`
              rather than a tint (#598). */}
          <div className="flex flex-wrap items-center gap-2 rounded-lg border border-blue-300 bg-blue-50 px-3 py-2 text-sm text-blue-900 shadow-xl dark:border-blue-700 dark:bg-blue-950 dark:text-blue-100">
            <span>{t("extendPromptText")}</span>
            {extendAsk.deadlineAt != null && (
              <span className="text-xs text-blue-700 dark:text-blue-300">
                {t("extendEndingIn", { seconds: Math.max(0, Math.ceil((extendAsk.deadlineAt - Date.now()) / 1000)) })}
              </span>
            )}
            <div className="ml-auto flex gap-2">
              <button
                type="button"
                onClick={keepRecording}
                className="rounded bg-blue-600 px-2 py-1 text-xs text-white hover:bg-blue-700"
              >
                {t("extendKeep")}
              </button>
              <button
                type="button"
                /* A user pressing Stop needs no toast telling them the meeting is over - they just said so.
                   Wrapped rather than passed bare: `stop` takes an optional StopReason, and the bare reference
                   would take the click's SyntheticEvent as that argument. */
                onClick={() => stop()}
                /* Carries its own background rather than being outline-only: a transparent fill would let
                   the page behind the panel show through the button itself (#598). */
                className="rounded border border-blue-400 bg-white px-2 py-1 text-xs hover:bg-blue-100 dark:border-blue-700 dark:bg-blue-900 dark:hover:bg-blue-800"
              >
                {t("extendStopNow")}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Recovery banners float below the bar in a popover. They must stay out of the capture bar's flow: it
          is a fixed-height bar, so an in-flow banner grows it and pushes the page down. */}
      {!recording && (pending || notesAttach || shotsAttach) && (
        <div
          data-testid="recorder-popover"
          className="absolute left-1/2 top-full z-40 mt-1 w-[28rem] max-w-[calc(100vw-2rem)] -translate-x-1/2 space-y-2"
        >
          {pending && (
            <div className={RECOVERY_BANNER}>
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
                  /* Carries its own background rather than being outline-only: a transparent fill would
                     let the page behind the banner show through the button itself (#601). */
                  className="rounded border border-amber-400 bg-white px-2 py-1 text-xs hover:bg-amber-100 disabled:opacity-50 dark:border-amber-700 dark:bg-amber-900 dark:hover:bg-amber-800"
                >
                  {t("recDiscardPending")}
                </button>
              </div>
            </div>
          )}
          {/* Notes attached-failure banner: the audio uploaded, the lines are safe - offer a retry. */}
          {notesAttach && (
            <div className={RECOVERY_BANNER}>
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
            <div className={RECOVERY_BANNER}>
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
