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
    version: "0.265.8",
    date: "2026-09-02",
    pr: 725,
    headline: "Finished work no longer leaves Diariz looking unfinished",
    summary:
      "An action item you had ticked off still went out looking like work you owed. Export or email a transcript and the finished action sat in the list with the others, nothing marking it as done - and the assistant was given the same undifferentiated list, so asking what was still outstanding could return things you had already finished.\n\nInside Diariz the tick was right there on screen. It was the same action, described two different ways depending on whether you were looking at it or sending it somewhere.\n\nCompleted actions are now marked wherever a meeting leaves the app - in exported and emailed transcripts, in the context the assistant reads, and in formula runs. The marker sits on the action itself and is translated, so the exports keep their existing layout.",
    fixed: [
      "Exported and emailed transcripts now mark an action item that has been ticked **Done**, instead of listing it indistinguishably from an outstanding one.",
      "The assistant is told which action items are already finished, so asking what is outstanding no longer returns completed work. The same applies to formula runs.",
    ],
  },
  {
    version: "0.265.7",
    date: "2026-09-02",
    pr: 727,
    headline: "A search test that was really testing the hardware",
    summary:
      "A test covering the index behind semantic search was failing on the build server while passing on a developer machine, on changes that had nothing to do with search. Because it is one of the checks that gates merging, it was holding up unrelated work.\n\nIt was not a fault in Diariz, and it was not random. The index is a deliberately approximate one: it trades a little accuracy for a lot of speed, and how much accuracy it gives up depends partly on the machine that built it. The test measured that accuracy and required a fixed figure, so it was really asking a question about the hardware. The same query over the same data scored near-perfect on one machine and around half on another.\n\nThe measurement has been removed. What it was there to protect - that a search never quietly loses results you are entitled to see because of who you are or which meeting you asked about - is still covered, by tests that do not depend on the machine underneath them.",
    fixed: [
      "Removed a test that required a fixed accuracy figure from an approximate search index. The figure depends on the machine, so it failed on the build server and passed locally, blocking unrelated changes. The behaviour it guarded is still covered.",
    ],
  },
  {
    version: "0.265.6",
    date: "2026-09-02",
    pr: 722,
    headline: "Merging a meeting no longer reopens finished work",
    summary:
      "Ticking an action item as done, and then merging that meeting into another, lost the tick. The action itself came through the merge intact - its wording, its owner, its deadline - but arrived un-ticked, with its completion date blank.\n\nThat is worse than it sounds. Merging two halves of one meeting is a filing job, not a reason to reopen anything, and the reopened item does not just look wrong on the meeting page: it comes back in your cross-meeting Actions list as outstanding, where Hide completed no longer hides it. So Diariz told you that you still owed something you had already finished, and kept telling you.\n\nCompletion now travels with the action through a merge, the date along with the tick, exactly as the wording and owner always did. Existing merged recordings are not revisited - anything already reopened this way needs ticking again.",
    fixed: [
      "Merging recordings kept each action item's wording, owner and deadline but dropped whether it was **done**, so finished work reappeared as outstanding in the Actions list and could not be hidden. The tick and its completion date now survive a merge.",
    ],
  },
  {
    version: "0.265.5",
    date: "2026-09-01",
    pr: 721,
    headline: "Two dependency records that no longer match the build",
    summary:
      "Diariz's server code records the exact version of every third-party library a build resolved to, in a set of lock files kept beside each project. A dependency refresh earlier today updated four of those files and left two behind.\n\nNothing was broken by that, which is precisely the problem: a build from a clean checkout quietly resolved something different from what was written down, rewrote the file to match, and carried on. Anyone building the project found their working copy modified before they had touched anything, and the record of what a release was actually built from was wrong for two of the six projects.\n\nBoth are back in step. Nothing running in Diariz changes version: one of the two covers the shared test helpers, and the other moves a build-time tool used when packaging the desktop app's Outlook helper, not anything the app runs.",
    fixed: [
      "Two of the six .NET lock files were left behind by an earlier dependency refresh, so a clean build resolved different versions from the ones recorded and rewrote them. Both now match what everything else already used.",
    ],
  },
  {
    version: "0.265.4",
    date: "2026-09-01",
    pr: 720,
    headline: "Two search tests that failed for reasons of their own",
    summary:
      "Two tests covering the semantic search index passed or failed depending on nothing more than the order the test suite happened to run them in. They were not finding a fault when they failed, and they were not proving anything when they passed.\n\nBoth check that a search can use the index that makes it fast. Postgres only reaches for that index once there is enough data to be worth it, which is correct - but the tests seeded well under that much and were quietly relying on other tests in the same file to top the database up first. Run in a different order, they ran against a near-empty table and failed. Each now seeds its own data, so it holds whatever runs before it.\n\nThe second test was wrong in a more interesting way. The index is deliberately approximate - it trades a little accuracy for speed - and the test demanded its answer match the exact one perfectly. That passed only while the table was small enough for the approximation to lose nothing, and broke as it grew, again reporting a fault that was not there. It now measures how much of the exact answer the fast path finds and requires the great majority of it, which is the thing actually worth guaranteeing.\n\nNothing about Diariz itself changes. The value is that these tests can no longer fail for reasons unrelated to the code, and had already blocked an unrelated dependency upgrade by doing so.",
    fixed: [
      "Two tests covering the semantic search index depended on how much data earlier tests had left behind, so they passed or failed on test ordering alone. Each now seeds enough of its own.",
      "The recall test required the approximate index to match the exact answer item for item, which it does not promise to do. It now measures the share of true results found and requires at least 80% of them.",
    ],
  },
  {
    version: "0.265.3",
    date: "2026-09-01",
    pr: 718,
    headline: "A finished meeting no longer looks like it was never transcribed",
    summary:
      "Two faults that between them could leave a finished meeting looking as though it had never been transcribed.\n\nPressing Stop wiped the live transcript off the screen. The full transcript is written as a new version of the same recording, and that new version was taking over the moment the work was queued - while it was still empty. So the text you had been reading vanished and nothing replaced it until the full pass finished, which on a long meeting is many minutes. If that pass then failed, the recording showed an empty transcript for good, even though everything said in the meeting was still safely stored. A transcript with nothing in it no longer replaces one with something in it.\n\nThe second was rarer and worse. While the server works on a long recording it periodically renews its claim on the job, so nothing else steals it. Each renewal was being counted as a failed attempt, and after about three minutes of entirely healthy work the job looked like one that had been crashing repeatedly. If the server restarted at any point after that, the recovery routine threw the job away rather than resuming it - and the recording stayed stuck with no transcript. Renewing a claim is no longer counted as a failure.",
    fixed: [
      "Stopping a recording made the live transcript disappear, leaving the recording apparently empty until the full transcript was ready - and permanently, if it never was.",
      "A long transcription could be discarded by the very mechanism that protects it, leaving the recording stuck with no transcript after a server restart.",
    ],
  },
  {
    version: "0.265.2",
    date: "2026-09-01",
    pr: 714,
    headline: "Live transcript: a real tab name, honest waiting, and no leftovers",
    summary:
      "Three things about the live transcript, all reported from a real meeting.\n\nThe Transcript tab was labelled with an internal name instead of the word Transcript. It now reads properly, in every language.\n\nBefore the first text arrived, the panel said nothing had been said yet - a statement about the room, and usually a wrong one, since people have generally been talking for a while by then. It now says what is actually true: Diariz is waiting for the first stretch of transcript, and that takes a moment.\n\nStarting a second recording showed the previous meeting's transcript until the new one caught up. The tab now starts empty for each meeting.\n\nAlso fixed, and less visible: when Diariz stopped transcribing live because it had fallen too far behind, it said so to nobody - the panel carried on claiming it was keeping up. That notice now reaches the screen, so a transcript that has quietly stopped tells you rather than looking merely slow.\n\nAnd the notes window you pop out into its own window now carries the Transcript tab too. It is the window people use precisely because a call has taken the screen, so it was the one that most needed it. Both windows read from the same source, so they cannot end up telling you different things about the same meeting.",
    fixed: [
      "The live transcript tab showed an internal name (liveTranscriptTab) rather than Transcript.",
      "Before any text arrived, the panel claimed nothing had been said - when what was true is that Diariz had not transcribed anything yet.",
      "Starting a second recording showed the **previous meeting's transcript** until the new one produced its own.",
      "A notice that live transcription had paused never reached the screen, so a stopped transcript was indistinguishable from a slow one.",
      "The **popped-out notes window** had no Transcript tab - the one window someone uses because a call has the screen, and so the one that most needs it.",
    ],
  },
  {
    version: "0.265.1",
    date: "2026-09-01",
    pr: 710,
    headline: "Recordings show the time you actually made them",
    summary:
      "A meeting could be listed at the wrong time - usually hours earlier than you recorded it, and by an odd amount that was neither a whole hour nor a time-zone difference. It only happened from the second recording onwards in a window you had not reloaded in between, which is why it looked so random: the same take made after a refresh came out right every time.\n\nWhat it was showing was the start time of your *previous* recording. Since recordings started uploading as the meeting runs, the start time is sent to the server the moment you press Record, and it was being read a fraction too early - before the new take had stamped its own. The first recording after opening Diariz had nothing to inherit and was always correct; every one after it borrowed the one before.\n\nThe audio, the transcript and the recording itself were never affected, only the time shown against it. That time also decides which calendar meeting a recording is matched to, so an affected recording could be linked to the wrong meeting or to none at all.\n\nRecordings made from now on carry their own start time. Ones already stored keep the time they were saved with.",
    fixed: [
      "A recording is stamped with **its own start time** rather than the previous recording's. Affected every recording after the first in a window that had not been reloaded.",
      "Calendar matching for those recordings now looks around the right time, so a meeting is no longer missed or mismatched because the recording appeared to be hours old.",
    ],
  },
  {
    version: "0.265.0",
    date: "2026-09-01",
    pr: 708,
    headline: "The live transcript knows who is talking",
    summary:
      "The live transcript arrived last release as a wall of text with no names on it. That was deliberate: the transcriber works on the meeting in half-minute slices and tells the voices apart inside each one, but it has no way to know that the first speaker in this slice is the same person as the first speaker in the next. Showing those numbers would have had everyone reshuffling every thirty seconds.\n\nDiariz now follows a voice across the whole meeting, so a speaker keeps one identity from start to finish - and where that voice belongs to someone already enrolled, they are named rather than numbered. The transcript you read during the meeting now reads like a conversation.\n\nIt corrects itself as it goes. A voice heard badly in one slice can be filed as a second speaker by mistake; when later audio makes clear it was one person all along, the earlier lines are joined back up in front of you. And where it is not certain who someone is, it says so - a guessed name is shown as a guess, not stated as fact.\n\nOne thing it deliberately never does: recognising a voice during a meeting never teaches Diariz that voice. Voice recognition is shared across everyone on your Diariz, so a name learned from thirty seconds of half-finished transcript would change recognition for every colleague in every future meeting. Training still happens only when a person confirms who someone is.",
    added: [
      "**Speaker labels in the live transcript**, stable for the whole meeting rather than resetting every half minute.",
      "An **enrolled voice is named** during the meeting, using the same recognition as a finished recording.",
      "A name Diariz is unsure of is shown as a **question rather than a statement**, so a guess reads as a guess.",
    ],
    changed: [
      "When later audio shows that two live speakers were one person, the earlier lines are **joined back together in front of you** rather than staying split.",
      "Recognising a voice live never trains that voice. Voice recognition is shared across everyone on your Diariz, so it still only learns when a person confirms who somebody is.",
    ],
  },
  {
    version: "0.264.0",
    date: "2026-09-01",
    pr: 705,
    headline: "Read the meeting while you are still in it",
    summary:
      "Diariz has always been something you looked at afterwards. The transcript arrived once the meeting had finished, which is the point at which you least need it: the moment you actually want to check what someone said twenty minutes ago is while they are still in the room.\n\nThe transcript is now written as the meeting runs. Open the notes panel while recording and there is a Transcript tab beside your notes, filling in every half minute or so with what has been said. You can also ask the assistant about a meeting that is still going, and it answers from the conversation so far.\n\nTwo things it is careful about. It does not put names to voices yet, because who is who only becomes reliable once there is a whole meeting to compare against, and a transcript that reshuffled its speakers every thirty seconds would be worse than one that stays quiet about them. And it never presents itself as the finished article: the status line tells you how far behind it is, and the full transcript still replaces it when you press Stop. If the server falls a long way behind it stops writing the live text and says so, which never affects the recording itself.",
    added: [
      "A **live transcript** while you record, in a new Transcript tab in the notes panel, updating as the meeting goes.",
      "The assistant can be asked about a **meeting that is still running**, and is told the conversation is unfinished so it does not report things as settled when they are still being argued about.",
      "A status line showing how far behind the live text is, and saying so plainly when it has stopped keeping up.",
    ],
    changed: [
      "The live transcript deliberately shows no speaker names. Attribution arrives once it can be made to hold across a whole meeting.",
    ],
  },
  {
    version: "0.263.1",
    date: "2026-09-01",
    pr: 689,
    headline: "A test that cried wolf, and the 83 MB it was waiting for",
    summary:
      "One test in the suite checked that the API reference opens inside Diariz rather than throwing you out to a new browser tab. To do it, it was loading the entire library that draws that reference - about 83 MB of it - when the only thing it looks at is the window the reference opens in.\n\nOn a cold machine that load could take longer than the test was allowed, so it failed for reasons that had nothing to do with what it was checking. It now uses a stand-in, which is what the reference's own test has always done. Nothing about Diariz changes: the suite is simply faster and stops failing at random, which matters because a test that cries wolf is how a real fault gets waved through.",
    fixed: [
      "A test covering the API reference intermittently failed, having spent its whole time budget loading a large third-party library it never inspected. Two test files pulled it in for real; both now use a stand-in, and nothing in the suite loads it any more.",
    ],
  },
  {
    version: "0.263.0",
    date: "2026-09-01",
    pr: 687,
    headline: "Your recording is saved as you go, not all at once at the end",
    summary:
      "Until now nothing reached Diariz until you pressed Stop. The whole meeting sat in your browser, and if the tab crashed, the laptop slept, or you closed the window by mistake, all of it went with them. A long meeting also had to go up in one push at the end, which is the slowest and least convenient moment for it.\n\nRecordings are now sent in pieces while the meeting runs. The recording appears in your list the moment you press Record and fills in as you talk, so at any point what has already been said is safe on the server rather than only in your browser.\n\nIf something does interrupt you, at most the last few seconds are lost instead of the entire meeting - and a recording whose window disappears is finished off automatically, with everything that arrived. There is nothing new to click: press Record and Stop exactly as before.\n\nThis also takes the weight out of long meetings. A three-hour recording no longer builds up in the browser as you go, and there is no large upload waiting for you at the end.",
    added: [
      "Recordings are **uploaded as the meeting runs**, so what has been said is already saved rather than waiting in your browser until you stop.",
      "A meeting whose window closes unexpectedly is **finished off on its own** from everything that arrived, instead of being lost.",
    ],
    changed: [
      "Stopping a recording is now quick even for a long meeting, because the audio has been arriving all along instead of going up in one push at the end.",
      "Long meetings no longer build up in the browser tab while you record.",
    ],
    fixed: [
      "A crashed tab, a sleeping laptop or a closed window during a meeting lost the entire recording. Nothing had reached Diariz until Stop was pressed, so there was nothing to recover.",
    ],
  },
  {
    version: "0.262.2",
    date: "2026-09-01",
    pr: 688,
    headline: "Choosing Ungrouped now sticks the first time you save it",
    summary:
      "If you had never changed a setting before, going into Preferences, setting **where new recordings go** to **Ungrouped**, and saving would quietly put it back to **Selected folder** - and the next thing you recorded was filed into whichever folder you happened to have open, rather than staying loose.\n\nSaving it a second time worked, so it looked like a glitch rather than something that happens every time. It only ever caught the very first setting a new account saved, and only that one choice. It now sticks the first time.",
    fixed: [
      "Setting **where new recordings go** to **Ungrouped** did not stick when it was the first setting an account had ever saved: it came back as **Selected folder**, and recordings were filed into the open folder instead of staying ungrouped. Saving it again worked, which is what made it look intermittent.",
    ],
  },
  {
    version: "0.262.1",
    date: "2026-09-01",
    pr: 686,
    headline: "The voice-matching releases become a chapter of their own",
    summary:
      "The release notes started opening on chapters last week, with everything since the previous chapter still listed loose at the top. That loose list had quietly grown to thirty-one releases, which is longer than most of the chapters underneath it.\n\nThe twenty-eight releases covering the voice-matching work are now a chapter of their own, so the top of the page is short again. Nothing was rewritten or thrown away: open the chapter and every one of those releases is there, with the same notes and the same links.",
    changed: [
      "The release notes have a new chapter, **Review Voice Matches**, covering the twenty-eight releases from 0.249.0 to 0.259.13. Those releases used to sit loose at the top of the page.",
    ],
  },
  {
    version: "0.262.0",
    date: "2026-08-29",
    pr: 679,
    headline: "Right-click a misspelled word in the desktop app and fix it",
    summary:
      "The desktop app has always underlined misspelled words in red, the way a browser does. Right-clicking one did nothing at all, so the underline told you something was wrong and then left you to work out what.\n\nRight-click now opens a menu with the spelling suggestions, and picking one replaces the word. If the word is right and Diariz simply does not know it - a name, a piece of jargon - **Add to dictionary** teaches it and the underline goes away for good.\n\nThe same menu carries **Cut**, **Copy**, **Paste** and **Select all**, which on Windows the desktop app did not offer anywhere before, and **Copy** on its own when you right-click text you have selected but cannot edit, such as a transcript.\n\nThis is a desktop app change, so it arrives with the next installer rather than the next time you refresh.",
    added: [
      "A **right-click menu** in the desktop app: spelling suggestions for the word under the cursor, and **Add to dictionary** for words Diariz does not know.",
      "**Cut**, **Copy**, **Paste** and **Select all** on right-click in any text box, and **Copy** on selected text you cannot edit. On Windows these were not available anywhere in the app before.",
    ],
    fixed: [
      "Right-clicking a word underlined as misspelled did nothing. The underlines came from the browser engine the desktop app is built on, but its menu of corrections is part of a browser's own window furniture, which the app does not include - so the suggestions existed and had nowhere to appear.",
    ],
  },
  {
    version: "0.261.0",
    date: "2026-08-28",
    pr: 677,
    headline: "The Actions tab is now a list you choose, not one you inherit",
    summary:
      "Every action item Diariz found was going straight into the Actions tab, across every meeting you have ever recorded. That made it a list of hundreds, most of it minor and much of it somebody else's, and no amount of filtering fixed it - the problem was that things arrived there on their own.\n\nNow they arrive because you put them there. Every action is still pulled out of every meeting and still sits on that meeting's page exactly as before, with nothing hidden and nothing lost. Each one has gained a pin, and pinning is what promotes it into the Actions tab and into a folder's Actions tab. Unpin it and it goes back to living on its meeting.\n\nThat means both of those views start empty, which is the point. They fill up with the handful of things you have actually decided to track.",
    added: [
      "A **pin** on every action item, on its meeting's page and in the Actions tab itself. Pinned actions appear in the cross-meeting Actions views; everything else stays on the meeting it came from.",
    ],
    changed: [
      "The **Actions** tab and a folder's **Actions** tab now list pinned actions only. Every action is still on its own meeting's page, unchanged - nothing has been deleted or hidden at source.",
      "For anyone using the API or the n8n node, the action list is unchanged by default, so existing automations keep seeing everything. Ask for `pinned=true` if you want just the pinned ones.",
    ],
  },
  {
    version: "0.260.0",
    date: "2026-08-28",
    pr: 675,
    headline: "The release notes open on the story, not on 496 rows",
    summary:
      "The release notes were one long list of every version ever shipped - 496 of them, newest first, which is a lot of scrolling to find out when a feature arrived.\n\nThey now open on the story instead: thirty named chapters, newest first, each covering a stretch of work with a short description of what changed in it and how many releases it took. Rooms, Formulas, connecting Claude over MCP, People, the calendar work - each is one card you can read rather than twenty rows you have to piece together. Click one and you get every individual release inside it, exactly as before: the same list, the same notes, the same links to the pull request behind each one. Nothing was rewritten or thrown away.\n\nThe other half of this is speed, and it applies whether or not you ever open the page. All of that history used to be loaded by every visitor on every page, because it travelled with the app itself. It now loads only when you go looking for it, which takes about a fifth off what your browser downloads before Diariz can start.",
    added: [
      "The release notes now open on **thirty named chapters** covering the history so far, newest first - each with a summary, its version range, and how many releases it covers.",
      "Opening a chapter lists **every release in it**, unchanged: the same notes, and the same link to the pull request behind each one.",
    ],
    changed: [
      "Diariz downloads about **a fifth less** before it can start. The release history used to travel with the app and load for everyone on every page; it now loads only when you open it.",
    ],
  },
];
