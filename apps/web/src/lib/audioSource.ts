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

// Coarse kind kept for the Electron tray + upload-title path (the tray only knows mic vs system).
export type AudioSourceKind = "mic" | "system";

export const isElectron = Boolean((window as any).diariz?.isElectron);

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
// mic capture applies the chosen device id + DSP/channel constraints.
export async function getStream(
  selection: SourceSelection,
  constraints?: AudioConstraints,
): Promise<MediaStream> {
  if (selection.kind === "system") return getSystemStream();
  const mtc = constraints ? toMediaTrackConstraints(constraints) : undefined;
  return getMicStream(selection.kind === "device" ? selection.deviceId : undefined, mtc);
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
  if (source === "system" && !electron) return "System audio capture needs the desktop app.";

  const name = (e as { name?: string } | null)?.name;
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
