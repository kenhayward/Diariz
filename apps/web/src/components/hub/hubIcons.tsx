import type { ReactNode } from "react";

// Command-hub glyphs (Feather/Lucide-style, 18px, drawn in `currentColor` so the host button's own text
// colour - including its disabled opacity - applies). Auto-stop = clock, Upload = tray/upload-arrow,
// Notes = pencil, Screenshot = camera, and the narrow-window overflow menu = three dots.
//
// These live here rather than in Recorder.tsx because the same four controls appear twice: as icon buttons
// in the bar, and as rows in MoreControlsPopover when the bar is too narrow to hold them. Two sets of
// hand-drawn paths for one control is how the bar and the menu drift apart.
// (The record/pause/resume/stop glyphs are RecordHero's and stay there - they have no menu equivalent.)
export function HubIcon({ children }: { children: ReactNode }) {
  return (
    <svg viewBox="0 0 24 24" width="18" height="18" fill="none" aria-hidden="true" focusable="false"
      stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
      {children}
    </svg>
  );
}

export const IconClock = () => (
  <HubIcon>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 7.5V12l3 2" />
  </HubIcon>
);

export const IconUpload = () => (
  <HubIcon>
    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12" />
  </HubIcon>
);

export const IconPencil = () => (
  <HubIcon>
    <path d="M17 3a2.83 2.83 0 0 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z" />
  </HubIcon>
);

export const IconCamera = () => (
  <HubIcon>
    <path d="M9 4l-1.5 2H4a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-3.5L15 4H9z" />
    <circle cx="12" cy="13" r="3.5" />
  </HubIcon>
);

/// Three dots - the conventional "there is more here" mark, and the only glyph in this set that names a
/// menu rather than an action.
export const IconMore = () => (
  <HubIcon>
    <circle cx="5" cy="12" r="1.4" fill="currentColor" stroke="none" />
    <circle cx="12" cy="12" r="1.4" fill="currentColor" stroke="none" />
    <circle cx="19" cy="12" r="1.4" fill="currentColor" stroke="none" />
  </HubIcon>
);
