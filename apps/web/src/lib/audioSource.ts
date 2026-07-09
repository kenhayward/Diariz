// Audio capture sources. Microphone works in any browser. "System" (loopback)
// capture relies on getDisplayMedia with audio — fully supported only inside the
// Electron shell (the main process installs a loopback display-media handler on
// Windows). In a plain browser it falls back to tab/screen audio where available.

import {
  toMediaTrackConstraints,
  normalizeInputDevices,
  type AudioConstraints,
  type InputDevice,
  type SourceSelection,
} from "./audioDevices";

// Coarse capture kind (mic / system-only / both) - defined with the source selection types.
export type { AudioSourceKind } from "./audioDevices";
import type { AudioSourceKind } from "./audioDevices";

export const isElectron = Boolean((window as any).diariz?.isElectron);

/// A capture in progress: the stream fed to MediaRecorder + tapped by the level meter, and a `stop` that
/// releases every underlying track (and any AudioContext, for the mixed combined stream).
export interface CaptureSession {
  stream: MediaStream;
  stop: () => void;
}

function trackSession(stream: MediaStream): CaptureSession {
  return { stream, stop: () => stream.getTracks().forEach((t) => t.stop()) };
}

/// Whether this environment can capture display/system audio at all. `getDisplayMedia` exists in Chromium
/// and the desktop shell (and Firefox, though it yields no audio track); it is absent in Safari. The UI uses
/// this to show/hide the "System audio" checkbox + the "No microphone" option.
export function supportsDisplayAudio(nav: Navigator = navigator): boolean {
  return typeof nav?.mediaDevices?.getDisplayMedia === "function";
}

export async function getMicStream(
  deviceId?: string,
  constraints?: MediaTrackConstraints,
): Promise<MediaStream> {
  // No device + no constraints ⇒ exactly today's behaviour (OS-default input, browser DSP defaults).
  if (!deviceId && !constraints) return navigator.mediaDevices.getUserMedia({ audio: true });
  const audio: MediaTrackConstraints = { ...(constraints ?? {}) };
  if (deviceId) audio.deviceId = { exact: deviceId };
  return navigator.mediaDevices.getUserMedia({ audio });
}

export async function getSystemStream(): Promise<MediaStream> {
  // Electron's setDisplayMediaRequestHandler grants the system audio loopback track.
  const stream = await navigator.mediaDevices.getDisplayMedia({
    video: true, // many platforms require a video track to be requested for audio
    audio: true,
  });
  // We only keep audio; stop any captured video tracks immediately.
  stream.getVideoTracks().forEach((t) => t.stop());
  return new MediaStream(stream.getAudioTracks());
}

// Capture from a resolved selection. System loopback ignores the mic constraints (they don't apply);
// mic capture applies the chosen device id + DSP/channel constraints. Returns a CaptureSession so the
// caller has one uniform teardown across mic / system / combined.
export async function getStream(
  selection: SourceSelection,
  constraints?: AudioConstraints,
): Promise<CaptureSession> {
  if (selection.kind === "system") return trackSession(await getSystemStream());
  const mtc = constraints ? toMediaTrackConstraints(constraints) : undefined;
  return trackSession(
    await getMicStream(selection.kind === "device" ? selection.deviceId : undefined, mtc),
  );
}

// Capture the selected microphone AND system audio, mixed into one track via the Web Audio API. The mic
// keeps its DSP constraints (echo cancellation on cuts loopback bleed); the destination is mono when the
// mono constraint is set. `stop` tears down both source streams and closes the AudioContext.
export async function getCombinedStream(
  selection: SourceSelection,
  constraints?: AudioConstraints,
): Promise<CaptureSession> {
  const mtc = constraints ? toMediaTrackConstraints(constraints) : undefined;
  const mic = await getMicStream(selection.kind === "device" ? selection.deviceId : undefined, mtc);

  let sys: MediaStream;
  try {
    sys = await getSystemStream();
  } catch (e) {
    mic.getTracks().forEach((t) => t.stop());
    throw e; // share cancelled/denied - the caller decides whether to fall back to mic-only
  }
  if (sys.getAudioTracks().length === 0) {
    // The browser shared a screen/tab but no audio (e.g. "Share audio" left unticked). Treat as "no system".
    sys.getTracks().forEach((t) => t.stop());
    mic.getTracks().forEach((t) => t.stop());
    throw new DOMException("No system audio track was shared.", "NotFoundError");
  }

  const ctx = new AudioContext();
  const dest = ctx.createMediaStreamDestination();
  if (mtc?.channelCount === 1) {
    dest.channelCount = 1;
    dest.channelCountMode = "explicit";
  }
  ctx.createMediaStreamSource(mic).connect(dest);
  ctx.createMediaStreamSource(sys).connect(dest);

  return {
    stream: dest.stream,
    stop: () => {
      mic.getTracks().forEach((t) => t.stop());
      sys.getTracks().forEach((t) => t.stop());
      void ctx.close();
    },
  };
}

export interface InputDeviceList {
  devices: InputDevice[];
  /** True once the browser exposes real device labels (only after a getUserMedia grant). */
  hasLabels: boolean;
}

// Enumerate microphone inputs. Before a getUserMedia grant, browsers withhold labels (empty strings)
// and often a stable device id, so `hasLabels` tells the UI whether it can show specific mics.
export async function listInputDevices(): Promise<InputDeviceList> {
  if (!navigator.mediaDevices?.enumerateDevices) return { devices: [], hasLabels: false };
  const all = await navigator.mediaDevices.enumerateDevices();
  // Drop the Windows default/communications aliases and strip hardware-id suffixes so the same
  // physical mic isn't listed three times with a "(vvvv:pppp)" code appended.
  const devices = normalizeInputDevices(
    all
      .filter((d) => d.kind === "audioinput" && d.deviceId)
      .map((d) => ({ deviceId: d.deviceId, label: d.label })),
  );
  const hasLabels = devices.some((d) => d.label !== "");
  return { devices, hasLabels };
}

export type MicPermission = "granted" | "denied" | "prompt" | "unknown";

// Query the mic permission where supported (Chromium). Firefox/Safari lack the "microphone" name →
// "unknown", so the UI degrades gracefully (offers the "Allow…" affordance rather than assuming).
export async function micPermissionState(): Promise<MicPermission> {
  try {
    const perms = (navigator as { permissions?: Permissions }).permissions;
    if (!perms?.query) return "unknown";
    const status = await perms.query({ name: "microphone" as PermissionName });
    return status.state as MicPermission;
  } catch {
    return "unknown";
  }
}

// A throwaway grant purely to unlock device labels (the affordance behind "Allow microphone to list
// devices…"). Tracks are stopped immediately; the caller re-enumerates afterwards.
export async function unlockDeviceLabels(): Promise<void> {
  const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
  stream.getTracks().forEach((t) => t.stop());
}

/// Turn a getUserMedia/getDisplayMedia failure into an actionable message. The browser reports the
/// cause via the DOMException `name`; the generic "could not access" hides it, which is unhelpful when
/// capture suddenly stops working (almost always another app holding the mic, or a revoked permission).
export function describeAudioError(e: unknown, source: AudioSourceKind, electron: boolean): string {
  const name = (e as { name?: string } | null)?.name;

  // System / combined capture goes through getDisplayMedia: a denied/cancelled share, or a share with no
  // audio track, is the common case - guide the user to the "Share audio" checkbox rather than claiming the
  // desktop app is required (it works in Chromium browsers too).
  if (source === "system" || source === "both") {
    switch (name) {
      case "NotAllowedError":
      case "SecurityError":
      case "PermissionDeniedError":
      case "NotFoundError":
      case "AbortError":
        return electron
          ? "Could not capture system audio. Please try again."
          : "No system audio was shared. In the share dialog, pick a screen or tab and tick 'Share audio' (Chrome or Edge), or use the desktop app.";
    }
  }

  switch (name) {
    case "NotAllowedError":
    case "SecurityError":
    case "PermissionDeniedError":
      return "Microphone access is blocked. Allow it for this site in your browser's address-bar/site settings, then try again.";
    case "NotFoundError":
    case "DevicesNotFoundError":
    case "OverconstrainedError":
      return "No microphone was found. Check it's connected and set as the default input device.";
    case "NotReadableError":
    case "TrackStartError":
    case "AbortError":
      return "The microphone is in use by another app, or unavailable. Close other apps using it (Zoom/Teams/another tab) and try again.";
    default:
      break;
  }

  // navigator.mediaDevices is undefined outside a secure context (http on a non-localhost host).
  if (typeof navigator !== "undefined" && !navigator.mediaDevices)
    return "Recording needs a secure page. Open the app via http://localhost (or https), not an IP address.";

  return "Could not access the audio source.";
}
