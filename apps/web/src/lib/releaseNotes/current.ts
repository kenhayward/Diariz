import type { Release } from "./types";

/// Releases since the last closed epoch, newest first. **This is the file every PR edits**: add the
/// new entry at the top, exactly as before.
///
/// `RECENT[0].version` must equal version.json (asserted in releases.test.ts). When this list grows
/// past a natural stopping point, close it as an epoch - write the record in `epochs.ts` and move
/// these entries to the top of `archive.ts`. releases.test.ts fails above 80 entries, which is a
/// safety net rather than the trigger; the historical epochs average 16.
export const RECENT: Release[] = [
  {
    version: "0.266.4",
    date: "2026-09-02",
    pr: 740,
    headline: "The speaker count on a meeting summary is the real one",
    summary:
      "A meeting summary could claim far more speakers than the meeting had - one with four people reported twelve - while the Speakers page beneath it listed the right number all along.\n\nDiariz keeps a record of every speaker label a recording has ever carried, on purpose: it is what lets a name you typed survive the recording being transcribed again. The summary tile was counting that history rather than the transcript in front of you, so a recording that had been transcribed more than once - which every live-captured meeting has been - counted the same people several times over.\n\nThe tile now counts the speakers actually heard in the transcript, and agrees with the page it links to.",
    fixed: [
      "A meeting summary could report many more speakers than it had - twelve for a four-person call - because it counted every speaker label the recording had ever carried rather than the ones in the current transcript. It now matches the Speakers page.",
    ],
  },
  {
    version: "0.266.3",
    date: "2026-09-02",
    pr: 741,
    headline: "The live-meeting releases become a chapter of their own",
    summary:
      "The twenty-three releases that turned Diariz from something you read afterwards into something you read during the meeting are now a chapter of their own, called Reading the meeting while you are still in it. The release notes page opens on it rather than on a list of individual releases.\n\nNothing is lost or shortened by this. Clicking the chapter still lists every one of those releases in full, exactly as they were written - the summary is a heading over them, not a replacement for them.\n\nThe chapter closes here because the arc it describes is finished: capture that survives a crash, a transcript you can read mid-meeting, speakers named while they talk, and the performance work that let all of it keep up on ordinary hardware. What is being looked at next - how accurately speakers are told apart - is the start of a different story.",
    changed: [
      "The **Reading the meeting while you are still in it** epoch now covers 0.260.0 to 0.266.2 - twenty-three releases, still listed in full when you open it.",
    ],
  },
];
