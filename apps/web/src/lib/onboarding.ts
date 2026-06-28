// First-run onboarding state + the guided-tour script. State is per-browser (localStorage), so the tour
// auto-shows once and can be replayed from the account menu.

export const ONBOARDED_KEY = "diariz.onboarded";

/// Whether this browser has already seen (or dismissed) onboarding. Fails safe to `true` so a storage
/// error never traps the user in a repeating tour.
export function hasOnboarded(): boolean {
  try {
    return localStorage.getItem(ONBOARDED_KEY) === "true";
  } catch {
    return true;
  }
}

export function setOnboarded(done: boolean): void {
  try {
    if (done) localStorage.setItem(ONBOARDED_KEY, "true");
    else localStorage.removeItem(ONBOARDED_KEY);
  } catch {
    /* ignore */
  }
}

/// One step of the tour. `target` matches a `data-tour="…"` attribute on the region to spotlight.
export interface TourStep {
  target: string;
  title: string;
  body: string;
}

export const TOUR_STEPS: TourStep[] = [
  {
    target: "capture",
    title: "Capture or upload audio",
    body: "Record from your microphone (or Windows system audio in the desktop app), or upload existing audio files. Everything is transcribed and diarized automatically.",
  },
  {
    target: "recordings",
    title: "Your recordings",
    body: "Recordings appear here as they finish. Organise them into sections, drag to reorder, and drop audio files anywhere on this panel to upload.",
  },
  {
    target: "detail",
    title: "Transcript & speakers",
    body: "Open a recording to read its speaker-labelled, timestamped transcript. Rename speakers, identify known people, edit, summarise, and play back.",
  },
  {
    target: "chat",
    title: "Chat across transcripts",
    body: "Ask questions across one or more transcripts with an AI assistant. Open this panel from its rail on the right.",
  },
  {
    target: "account",
    title: "Settings & more",
    body: "Configure your AI endpoint, manage enrolled speakers (People), check your storage, and replay this tour any time from here.",
  },
];

/// Copy for the empty detail page, chosen by how many recordings the user has.
export function emptyStateCopy(recordingCount: number): { title: string; body: string; showTour: boolean } {
  if (recordingCount === 0) {
    return {
      title: "Welcome to Diariz",
      body: "Press Record or Upload at the top to add your first recording — it’ll be transcribed and appear in the list on the left.",
      showTour: true,
    };
  }
  return {
    title: "Select a recording",
    body: "Choose a recording from the list, or press Record / Upload to add another.",
    showTour: false,
  };
}
