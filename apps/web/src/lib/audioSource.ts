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
