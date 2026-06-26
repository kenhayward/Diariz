// Audio capture sources. Microphone works in any browser. "System" (loopback)
// capture relies on getDisplayMedia with audio — fully supported only inside the
// Electron shell (the main process installs a loopback display-media handler on
// Windows). In a plain browser it falls back to tab/screen audio where available.

export type AudioSourceKind = "mic" | "system";

export const isElectron = Boolean((window as any).diariz?.isElectron);

export async function getMicStream(): Promise<MediaStream> {
  return navigator.mediaDevices.getUserMedia({ audio: true });
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

export async function getStream(kind: AudioSourceKind): Promise<MediaStream> {
  return kind === "system" ? getSystemStream() : getMicStream();
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
