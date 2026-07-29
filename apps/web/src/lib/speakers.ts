import type { SpeakerInfo } from "./types";

/// A speaker counts as "assigned" once it is linked to a person or has a display name that differs from
/// its raw diarization label (i.e. the user named or identified it, vs. the default "SPEAKER_00").
export function isSpeakerAssigned(s: SpeakerInfo): boolean {
  return s.personId != null || (s.displayName !== "" && s.displayName !== s.label);
}

/// True when there is at least one speaker and every one has been assigned — used to start the
/// detail page's speaker panel collapsed when there's no labelling left to do.
export function allSpeakersAssigned(speakers: SpeakerInfo[]): boolean {
  return speakers.length > 0 && speakers.every(isSpeakerAssigned);
}
