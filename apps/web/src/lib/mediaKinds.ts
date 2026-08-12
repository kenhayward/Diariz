import { fileExtension, MAX_UPLOAD_BYTES } from "./audioFormats";

// What a dropped file is, before anything is decoded. This is UX and ordering only - the server
// independently sniffs the actual bytes of whatever finally gets uploaded.
//
// The split matters: type and a sanity ceiling are judged on the SOURCE (which may be a multi-GB
// video), while the 500 MB upload cap is judged on the RESULT (the extracted audio). Judging both on
// the source rejected every real webinar before extraction could shrink it.

/// `audio` uploads unchanged; `container` may hold a video track and needs a peek; `rejected` cannot
/// be demuxed in the browser at all.
export type MediaKind = "audio" | "container" | "rejected";

/// Formats that are audio by definition, uploaded byte-for-byte as today. `.m4a`/`.aac` stay here
/// deliberately: they are ISO-BMFF and could in principle carry a track, but treating them as
/// containers would mean re-encoding ordinary audio uploads over embedded cover art.
const AUDIO_EXTENSIONS = ["wav", "mp3", "flac", "ogg", "opus", "m4a", "aac"] as const;

/// Formats that may hold video. `.webm` is here rather than in the audio list because it is genuinely
/// ambiguous - the browser recorder writes audio-only WebM, but a screen capture is also WebM.
const CONTAINER_EXTENSIONS = ["mp4", "m4v", "mov", "mkv", "webm"] as const;

/// The `accept` attribute for the hidden <input type="file">.
export const MEDIA_ACCEPT_ATTR =
  [...AUDIO_EXTENSIONS, ...CONTAINER_EXTENSIONS].map((e) => `.${e}`).join(",") + ",audio/*,video/*";

/// Sanity ceiling on the dropped file. Well above the 2-6 GB target envelope; past it the browser
/// runs out of memory regardless, and a clear message up front beats a crash midway through.
export const MAX_SOURCE_BYTES = 8 * 1024 * 1024 * 1024;

export function classifyFile(file: { name: string }): MediaKind {
  const ext = fileExtension(file.name);
  if ((AUDIO_EXTENSIONS as readonly string[]).includes(ext)) return "audio";
  if ((CONTAINER_EXTENSIONS as readonly string[]).includes(ext)) return "container";
  return "rejected";
}

/// Judged on the dropped file, before extraction. Returns a message, or null to proceed.
export function sourceProblem(
  file: { name: string; size: number },
  maxBytes = MAX_SOURCE_BYTES,
): string | null {
  if (classifyFile(file) === "rejected")
    return "Unsupported file type. Use WAV, MP3, FLAC, Ogg/Opus, WebM, M4A, or a video (MP4, MOV, MKV). Convert anything else first.";
  if (file.size === 0) return "That file is empty.";
  if (maxBytes > 0 && file.size > maxBytes)
    return `File too large. The maximum is ${Math.round(maxBytes / (1024 * 1024 * 1024))} GB.`;
  return null;
}

/// Judged on what will actually be uploaded - the extracted audio for a video, or the original file
/// for an audio upload. Type is already settled by `sourceProblem`, so this is size only.
export function resultProblem(file: { size: number }, maxBytes = MAX_UPLOAD_BYTES): string | null {
  if (maxBytes > 0 && file.size > maxBytes)
    return `File too large. The maximum is ${Math.round(maxBytes / (1024 * 1024))} MB.`;
  return null;
}
