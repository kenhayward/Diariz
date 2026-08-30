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
    version: "0.263.0",
    date: "2026-08-30",
    pr: 684,
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
  {
    version: "0.259.13",
    date: "2026-08-28",
    pr: 674,
    headline: "Plain hyphens everywhere a reader can see",
    summary:
      "The About box described Diariz as built on open-source software using a long dash, and the chat `/help` list separated each command from its description with another. Small, but the house style is a plain hyphen, because feedback on the fancier ones has been consistently negative.\n\nSeven of them had crept in across three screens. They are gone, and a test now covers the rule everywhere it applies - the interface catalogues in every language, the release notes you are reading, and the source itself - rather than only the help articles, which was the one place anything checked. Comments written for developers are left alone, as they always were.\n\nNothing about how Diariz behaves has changed.",
    fixed: [
      "Long dashes appeared in the **About box** disclaimers, the chat **`/help`** command list, and the context dial placeholder. All seven are now plain hyphens.",
    ],
    added: [
      "A test that fails the build on an em or en dash in **any** user-facing text - interface catalogues (both the web and API ones), release notes, and source strings - not just help articles.",
    ],
  },
  {
    version: "0.259.12",
    date: "2026-08-28",
    pr: 672,
    headline: "Pin the last two version mirrors",
    summary:
      "Housekeeping with a real edge to it. The desktop and n8n `package-lock.json` files had been left on `0.197.4` while everything else tracked the release - about sixty versions of drift - because the test that pins the version across files only ever checked the web one.\n\nNothing shipped wrong: npm publishes from `package.json`, not the lock file. But this is the same drift the test exists to catch, and the n8n node has form here - it sat at `0.1.0` for roughly seventy releases, and an npm version cannot be corrected once it is out.\n\nAll three lock files are now pinned, in both places npm writes the version, and each assertion was checked by breaking it on purpose to be sure it can actually fail. The contributor notes were wrong too - they named four mirrors and no lock files, so following them exactly still left these behind. They now name all seven.\n\nNothing about how Diariz behaves has changed.",
    fixed: [
      "The **desktop and n8n `package-lock.json`** versions had drifted about sixty releases behind `version.json`. Both are now pinned, in both places npm writes the version.",
    ],
    changed: [
      "`versionMirrors.test.ts` now asserts **all three** lock files rather than only the web one, so this cannot drift again unnoticed. The release checklist in the contributor notes names all seven mirrors, and says to edit lock files by hand rather than regenerate them.",
    ],
  },
  {
    version: "0.259.11",
    date: "2026-08-28",
    pr: 671,
    headline: "Make a local test run tell the truth",
    summary:
      "A developer-facing fix, but the one behind the last two releases. On the Windows development machine `vitest` printed **nothing** a test logged - no warnings, no stray errors, nothing - while the identical run on Linux printed all of it. A local test run therefore proved nothing about whether the output was clean, and it was not: 143 React warnings accumulated over a fortnight behind that blind spot, every local run looking perfectly quiet the whole time.\n\nThe cause was leaving the reporter for vitest to choose. Named explicitly it prints on both platforms, and Linux was already choosing the same one, so nothing about the CI output changes. The suite is silent on both now, and for the first time that can be checked without pushing.\n\nNothing about how Diariz behaves has changed - this is test-tooling only.",
    fixed: [
      "`vitest` printed nothing a test logged to the console on the Windows dev machine, so a clean local run was not evidence that the output was clean. The reporter is now named explicitly, which restores it on Windows and leaves Linux and CI exactly as they were.",
    ],
  },
  {
    version: "0.259.10",
    date: "2026-08-28",
    pr: 670,
    headline: "Close the last gap in the test suite's silence",
    summary:
      "The last line of noise from the web test suite, and the one the previous release's guard could not catch. A passing run occasionally printed `The current testing environment is not configured to support act(...)` from one chat test - about one run in six, always the same test.\n\nIt is a different fault from the 143 warnings fixed in 0.259.9, which is why the guard added there passed over it: those were updates escaping an `act(...)` scope, this was a scope finishing with the act environment switched off underneath it. The cause was a testing-library query awaited *inside* `act`. Those queries turn the act environment off while they poll, so nesting one leaves the scope to close against a different environment than it opened with - and only sometimes, depending on whether the query had to poll at all.\n\nThe five places doing that now resolve the element first and act on it second, through one shared helper that carries the explanation. The guard has been widened to this second message as well, so neither form can return.\n\nNothing about how Diariz behaves has changed - this is test-suite work only.",
    fixed: [
      "A chat test intermittently logged **The current testing environment is not configured to support act(...)** on an otherwise passing run. Five places awaited a testing-library query inside an `act(...)` scope, which switches the act environment off while it polls; they now resolve the element first.",
    ],
    changed: [
      "The act guard added in 0.259.9 now catches **both** forms - an update escaping a scope, and a scope running without the environment - so a passing run stays silent either way.",
    ],
  },
  {
    version: "0.259.9",
    date: "2026-08-28",
    pr: 668,
    headline: "A silent test suite, and a guard to keep it silent",
    summary:
      "Every passing build was printing 143 React `act(...)` warnings. Each one is a piece of asynchronous work finishing outside the window the test around it was watching, so nothing was wrong with Diariz itself: the tests were starting work and then not waiting for it, and the updates landed after the test had already ended. That is the raw material a flaky test is made from, and the count had grown from 94 to 143 in a fortnight because nothing was stopping it.\n\nThey came from a handful of shared test helpers rather than from many separate mistakes: one that asked the recorder to start, one that delivered a screenshot from the desktop shell, and several that opened a panel which fetches as it mounts. Fixing those cleared most of them at a stroke.\n\nA guard now fails any test that lets an update escape, naming the line that updated, so the count cannot climb back. The rest of the noise a passing run produced has gone too, and a test that provokes an error on purpose now declares it instead of printing it.\n\nNothing about how Diariz behaves has changed - this is test-suite work only.",
    fixed: [
      "The web test suite emitted **143 React act(...) warnings** on a passing run. They were tests that did not await asynchronous work they had started, so state updates arrived after the test had finished.",
      "A passing run is silent again: query mocks that resolved `undefined`, an upload mock that returned nothing where the real call returns a recording, and a route the test router never defined.",
    ],
    added: [
      "A guard in the test setup **fails any test that lets a React update escape `act(...)`**, naming the source line responsible, so the warnings cannot creep back in.",
    ],
  },
  {
    version: "0.259.8",
    date: "2026-08-28",
    pr: 664,
    headline: "Say that a voiceprint is shared by everyone",
    summary:
      "A question worth answering in the help: **a voiceprint is shared, and what you do to it affects everyone.** There is one record per person across the whole platform rather than one per colleague - which is why erasing somebody is a single deletion - so the voice Diariz has learned is the average of every recording behind that person, whoever made them.\n\nThat means naming a speaker on your own transcript, or confirming one in Review Voice Matches, teaches a voiceprint your colleagues rely on too. It is the intended trade: only somebody who was in the meeting can say who a voice is, so it is not something to reserve for administrators. It is still worth knowing that it is what you are doing - listening before you answer is the whole of the discipline, because a voice confirmed unheard is how a voiceprint learns the wrong person and starts naming them in other people's recordings.\n\nThe help now says so, along with the two guarantees that go with it: nobody can enrol a person who has opted out, and anything dropped from training is set aside rather than thrown away.",
    changed: [
      "The people help now explains that **a voiceprint is one shared record**, that naming or confirming a voice teaches the one your colleagues rely on, and why that is deliberate rather than an oversight. Documentation only - nothing about how Diariz behaves has changed.",
    ],
  },
  {
    version: "0.259.7",
    date: "2026-08-28",
    pr: 663,
    headline: "Say that removing a segment shapes the voiceprint",
    summary:
      "Taking a segment out in Review Voice Matches decides what the voiceprint learns from - the segments you leave in are the audio Diariz measures the voice from. Nothing on the panel said so. The control said **Take this segment out**, out of what unstated; the count said **1 segment excluded**, excluded from what unstated; and the paragraph at the top said confirming teaches the voiceprint without mentioning segments at all, so it read as though the whole recording was used whatever you did.\n\nA user asked outright whether removing segments did anything to voiceprints, having concluded from the panel that it did not. It does.\n\nThe wording now names the consequence wherever the choice is made, and says the other half too: **nothing is written while you are choosing.** Removals take effect only when you confirm the voice, and declining or closing the window discards them.",
    fixed: [
      "Nothing in **Review Voice Matches** said that taking a segment out changes what the voiceprint is trained from. The control now reads **Not them - leave this segment out of the voiceprint**, the count reads **1 segment left out of the voiceprint**, and the introduction says confirming teaches the voiceprint **from the segments you leave in** - and that nothing is saved until you answer.",
    ],
  },
  {
    version: "0.259.6",
    date: "2026-08-28",
    pr: 660,
    headline: "One rule for what trains a voiceprint",
    summary:
      "Each segment in Review Voice Matches had a tick and a cross. Only the cross did anything. Ticking ten segments and confirming trained the voiceprint from all of them - the ten and the ones never touched alike - because a segment counts unless you take it out, so ticked and untouched were the same state.\n\nThe tick is gone. It sat beside the cross, the same size, and turned green when pressed, so it read as choosing what would be used; it recorded nothing. What is left is one rule with nothing hidden in it: **a segment trains unless you remove it.**\n\nIn its place the panel now says what will happen, and says it from the start: **Confirming trains from 28 of 30 segments**. That line used to appear only once something had been removed - so the ordinary path, look and decide and confirm, was the one it said nothing about at all.",
    fixed: [
      "The per-segment tick in Review Voice Matches **did nothing**. It looked like the opposite of the cross beside it, and turned green when pressed, but a segment trains unless it is removed - so a ticked segment and one you never answered were identical. The tick has been removed rather than left to mislead.",
    ],
    changed: [
      "The panel states what confirming will train from **at all times** - *Confirming trains from 28 of 30 segments* - instead of only once a segment has been taken out.",
    ],
  },
  {
    version: "0.259.5",
    date: "2026-08-27",
    pr: 658,
    headline: "The AI settings fit on a screen",
    summary:
      "The AI settings page spent three lines and a gap on every setting - the name, then the control, then the description underneath - so a handful of settings ran well past the bottom of the window.\n\n**Each setting is now one line**: its name, the control, and the description, side by side. Every control sits in the same vertical line down the page, including the ones in the voice-identification section, so the page can be read down a column rather than hunted through. The dialog is wider to make room.\n\nMeasured on the AI tab: the content is **35% shorter** (975px to 638px), with all nine settings on a line each. Nothing was removed - every description is still there, at the end of its row. On a narrow window it falls back to the old stacked layout, because three columns cannot fit and a description squeezed into a few characters is worse than one on its own line.",
    changed: [
      "Every setting on **Settings -> AI** is now a single line - name, control, description - instead of three lines and a gap. The controls line up in one column down the whole page, including the voice-identification settings, and the dialog is wider to fit. The AI tab is **35% shorter** as a result; every description is kept.",
    ],
  },
  {
    version: "0.259.4",
    date: "2026-08-27",
    pr: 651,
    headline: "Two hardening fixes around uploaded filenames and worker logs",
    summary:
      "Two hardening fixes, from a static-analysis warning and the review it prompted. Neither exposes anything or lets anyone do something they could not already do; both are worth closing.\n\n**The name a file is stored under no longer trusts what the file was called.** Diariz works out what an upload actually is by decoding it, never by its name - but the name it filed the bytes under was still built from the client's filename, whatever characters that contained. That value also shows up in the download header when you save a recording. An extension that is not an ordinary one is now dropped; nothing needs it. This covers recordings, meeting attachments and folder attachments alike, since all three were built the same way.\n\n**A failure message from the transcription worker is cleaned before it reaches the log.** Everywhere else that logs text from outside already did this; this one place did not, so line breaks in such a message could add entries that read like Diariz's own.\n\nNothing already stored changes, and no file becomes unreachable: existing keys are kept as they are.",
    fixed: [
      "The name an uploaded file is **stored under** is no longer built from the client's filename without checking it - for recordings, meeting attachments and folder attachments alike. Only an ordinary extension is kept, which is safe because the format is determined by decoding the file rather than by its name. The same value appears in the **download** header for a recording, where the rest of the filename was already being cleaned and this part was not.",
      "A failure message from the transcription worker is now **cleaned before being logged**, as text from outside is everywhere else in Diariz. Line breaks in such a message could otherwise add entries to the log that read like Diariz's own.",
    ],
  },
  {
    version: "0.259.3",
    date: "2026-08-27",
    pr: 646,
    headline: "Review Voice Matches",
    summary:
      "The account-menu item for the voice review was showing **voicesToConfirm** - its internal name rather than a label. The wording it needed lived in one translation file while the menu was reading another, and with nothing to fall back on it printed the name of the missing entry.\n\nIt now reads **Review Voice Matches**, and so does the window it opens - both take their wording from the same place, so they cannot say different things again.",
    fixed: [
      "The account-menu item for the voice review read **voicesToConfirm** - an internal name, shown because the label it wanted was in a different translation file from the one the menu reads.",
    ],
    changed: [
      "**Voices to Confirm** is now called **Review Voice Matches**, in the menu and as the window's own heading. Both come from a single entry, so the two cannot drift apart.",
    ],
  },
  {
    version: "0.259.2",
    date: "2026-08-27",
    pr: 644,
    headline: "Listen and answer without crossing the panel",
    summary:
      "Working through the queue is listen, then decide, over and over. The play button sat at the far left of each segment and its yes and no at the far right, so every single segment cost a full traverse of the panel with the mouse.\n\nThe play button now sits **immediately left of the tick**, so the three controls are together at the end of the row: hear it, then answer, without moving. Nothing else about the row changes.",
    changed: [
      "In Voices to Confirm, each segment's **play button has moved to sit beside its yes and no** at the end of the row, rather than at the opposite end from them. Working through a queue is listen-then-decide repeatedly, and the two controls being apart cost a full mouse traverse per segment.",
    ],
  },
  {
    version: "0.259.1",
    date: "2026-08-27",
    pr: 643,
    headline: "Your segment marks stay put, and say how to keep them",
    summary:
      "Marking segments in and out, then glancing at another voice, threw the marks away. Coming back showed every excluded segment restored and every tick gone, with nothing said about it.\n\n**Your marks now stay with the voice they belong to.** Look at another voice and back, and they are exactly as you left them. They are still kept apart per voice - one voice's exclusions never reach another's voiceprint - which was the point of the original behaviour; it simply achieved it by discarding your work.\n\n**And it now says how to make them permanent.** There is no separate save: the tick beside the question is what commits, and it applies whatever is still in the list. That button now says **Confirm this voice** in words rather than being an icon with a tooltip, and while anything is excluded a line above it reads *Confirming trains from 4 of 5 segments*, so you can see what you are about to do before you do it.\n\nMarks live for as long as the window is open. Closing it without confirming still discards them - nothing is written until you confirm the voice.",
    fixed: [
      "Marking segments in or out in Voices to Confirm and then opening another voice **threw the marks away**. They are now kept per voice, so switching away and back leaves them exactly as you left them - while still never letting one voice's exclusions reach another's voiceprint.",
    ],
    changed: [
      "The tick and cross beside the question now read **Confirm this voice** and **Not this person** in words rather than being icons with a tooltip. Confirming is the only thing that commits, and there is no separate save, so the button had to say so.",
      "While any segment is excluded, the panel states what confirming will do - *Confirming trains from 4 of 5 segments* - instead of leaving it to be discovered afterwards on the person's Voiceprint tab.",
    ],
  },
  {
    version: "0.259.0",
    date: "2026-08-27",
    pr: 641,
    headline: "Say yes to a voice without saying yes to all of it",
    summary:
      "A diarization label is not always one human. Two people on one microphone, or a stretch of crosstalk, and the software puts it all under a single speaker - so being asked \"is this Ada?\" about the whole thing left you two bad options: accept audio that is plainly somebody else, or throw away an identification you know is right.\n\n**Every segment now has its own yes and no.** Say no and that segment leaves the list. What is left is what the voiceprint is trained from, so excluding a stretch actually keeps it out - it is not simply hidden from you. The tick and cross at the top still answer for the whole voice and clear it from the queue, as before, and **Restore** puts back anything excluded by mistake.\n\n**Play all** listens straight through the segments you have kept, one after another, highlighting each as it plays and scrolling it into view so you can follow along in a long list. Press it again to stop. Excluded segments are skipped.\n\nWorth knowing what excluding does **not** do: it shapes the voiceprint, not the transcript. Those segments still sit under the same speaker and still carry that person's name once you confirm the voice. Splitting one speaker into two people is a separate job.",
    added: [
      "**A yes and a no on every segment** in Voices to Confirm. Saying no takes that segment out of the list, and the voiceprint is then trained from only what is left - so a stretch that is somebody else is genuinely kept out, not just hidden. **Restore** puts back anything excluded by mistake.",
      "**Play all** plays the kept segments straight through, highlighting each one and scrolling it into view as it goes, so you can follow a long list without hunting for the row. Press it again to stop; excluded segments are skipped.",
    ],
    changed: [
      "The tick and cross beside the question now read **Confirm this voice** and **Not this person**, so they are not confused with the per-segment answers below them. They still decide the whole voice and clear it from the queue.",
    ],
  },
  {
    version: "0.258.0",
    date: "2026-08-27",
    pr: 640,
    headline: "Hear the voice you are being asked about",
    summary:
      "**Voices to Confirm is now a window, not a page.** It opens over whatever you were reading, the same way People does, and it no longer sends you somewhere else and back. It sits directly under Preferences in the account menu.\n\nIt is in two halves. The queue is on the left; opening one shows **what that voice actually said** on the right, a line per segment, each with a play button. Press it and you hear that moment. Press it again and it stops - one button, both states. The answer is a tick and a cross at the top of the same panel, so listening and deciding happen without moving.\n\nThis is deliberately kept **out of People**. Answering whether a voice belongs to someone needs no permission - you were in the meeting, which is the qualification that matters - whereas the People directory is gated on Manage people. Folding one into the other would have taken the queue away from everybody who cannot browse the directory.\n\n**Questions you cannot answer are no longer asked.** Recording audio is deleted once it passes the retention period, and the only honest way to say whether a voice is someone is to listen to it. A suggestion whose audio has gone is not a question, it is a permanent occupant of the list - so those no longer appear in the queue, and the transcript stops raising them too. Nothing is decided on your behalf: the suggestion is simply withheld.",
    added: [
      "**Voices to Confirm** is now a two-panel window opened from the account menu, directly below Preferences: the queue on the left, and on the right what that voice said, a line per segment with a play/stop button on each. Confirm or decline with a tick or a cross beside the question.",
      "Playback for a voice that has not been named yet. Each clip is cut on the server from that speaker's own segments and nothing else, in your own recordings only.",
    ],
    changed: [
      "The queue moves with you rather than replacing the page, so checking a voice no longer costs you your place in a transcript. Opening it selects the first voice straight away, and answering one moves on to the next.",
    ],
    fixed: [
      "Voices whose recording audio has already been deleted no longer appear in the queue, and the transcript no longer offers **Might be ... - is it?** for them. There is nothing left to listen to, so the question could not be answered - and those rows could never be cleared.",
    ],
  },
  {
    version: "0.257.0",
    date: "2026-08-26",
    pr: 637,
    headline: "Keep the audio a voiceprint was built from",
    summary:
      "Recording audio is deleted automatically once it passes the retention window your administrator sets. The transcript survives, which is the point - but it meant the recordings behind a voiceprint quietly lost the one thing needed to check them. On the platform this was found on, **47% of the recordings training a voiceprint could no longer be played**, and the figure grew every night.\n\nThat matters because confirming a recording really is the right person can only be done by ear. Nearly half the review list was asking for a judgement that had become impossible to make - and the **Play** buttons were still there, doing nothing when pressed.\n\n**Audio a voiceprint was enrolled from is no longer deleted automatically.** It is a small set - the recordings you actually enrolled someone from, not your library - and it is the evidence behind a biometric, so it is worth keeping. Deleting audio **by hand** is unchanged: the objection is to a background job removing it silently, not to you deciding you no longer want it.\n\nWhere the audio has already gone, the row now says **Audio deleted - cannot be played** and offers no play buttons. It still lists what was said, and you can still confirm the recording: you may well remember the meeting, and blocking it would leave those recordings permanently stuck in the review list.\n\nNothing here affects recognition. The voiceprints themselves were computed when the recording was transcribed and are unaffected - it is only listening back that the deleted audio prevents.",
    changed: [
      "Recordings that a voiceprint was enrolled from are now **exempt from automatic audio deletion**, so they can still be listened to when checking whether a voice really is that person. Deleting audio by hand is unchanged, and a recording stops being exempt once nothing trains from it.",
      "The recording page's \"audio will be deleted on\" note now accounts for that exemption instead of promising a deletion that will not happen.",
    ],
    fixed: [
      "A recording whose audio has already been deleted now says so on the person's Voiceprint tab, instead of offering **Play voice** and **Play segment** buttons that silently did nothing. It still lists what was said, and can still be confirmed.",
    ],
  },
  {
    version: "0.256.1",
    date: "2026-08-26",
    pr: 638,
    headline: "The Move to folder dialog uses the screen it has",
    summary:
      "**Move to folder** showed its folder list through a fixed window a little over seven rows tall, inside a dialog narrower than the names it had to fit. Anyone with more than a handful of folders scrolled a short slot in the middle of a mostly empty screen, and longer folder names ran out of room sideways.\n\nThe dialog now sizes itself against the screen instead: it stays where it always opened, centred, and grows down to about **85% of the window's height**, so a long folder tree is browsed rather than peeped at. It is also slightly wider, which is what stops the longer names being cut short.\n\nThe filter box at the top and the **New folder name** row at the bottom stay put while the list itself scrolls - so however far you scroll, the way out is still on screen. With only a few folders the dialog is no bigger than it needs to be; it grows to the cap only when there is something to show.\n\nThis is the **Move to folder** dialog only. The **Choose a folder** dialog in Preferences, which scrolls its whole body rather than just the list, is unchanged.",
    changed: [
      "The **Move to folder** dialog now grows to around 85% of the window height and is slightly wider, instead of capping its folder list at a fixed height. The filter box and the create-a-folder row stay pinned while the list scrolls.",
    ],
  },
  {
    version: "0.256.0",
    date: "2026-08-26",
    pr: 634,
    headline: "Does this recording sound like somebody else?",
    summary:
      "A person's card could tell you that a recording sounded unlike their others. It could not tell you the thing that matters: whether it sounds like **somebody else**. Those are different questions, and only the second separates the same voice on a different microphone from a different person enrolled under one name.\n\nMeasuring this platform's training data before building anything on it: of the recordings behind people who have more than one, **more than a quarter sit closer to a different person than to any of their own** - and a third of those are close enough to that other person to be matched as them. The old check flagged most but not all of them, and one read as perfectly healthy.\n\nSo each recording now also asks whether anyone else is closer, and **names them** when they are. Those recordings sort to the top of the list and to the top of the People directory's review ranking, ahead of ones that merely sound unlike the rest. Since 0.254.0 the same row can reassign it, so the answer and the fix sit together.\n\nThere is also a new tick box: **Confirmed as this person**. It records that a human listened and vouched for a recording, and takes it out of the review queue. It is deliberately separate from *Trains the voiceprint* - one asks who it is, the other asks whether the audio is worth learning from, and a recording can be genuinely them and still be too noisy to train on. There is no way to confirm a whole person at once, on purpose: the entire value is that somebody listened.\n\n**What this does not cover:** a person with only one recording is never flagged this way, however close somebody else sits. The evidence is comparative - closer to them than to their own - and one recording has nothing of its own to compare against. Most of a directory is usually in that state, so flagging on closeness alone would bury the real findings.",
    added: [
      "Each recording behind a voiceprint is now also checked against **everybody else's** recordings, and says whose it sounds more like when another person is closer. Those rows sort to the top, and the People directory ranks people with one above people whose recordings merely sound unlike each other.",
      "**Confirmed as this person** on each recording: a record that a human listened and vouched for it, which takes it out of the review queue. Separate from whether the recording trains the voiceprint, and revocable.",
    ],
    changed: [
      "The review ranking now leads with people who have a recording that sounds like somebody else, rather than with the most scattered voiceprints.",
    ],
  },
  {
    version: "0.255.0",
    date: "2026-08-26",
    pr: 632,
    headline: "Re-measuring a recording says whether it worked",
    summary:
      "Pressing **Recompute voiceprint** looked like it did nothing, and in the commonest case it genuinely said nothing at all. Whether a re-measure was running was worked out from two other columns, and selecting the whole speaker - the state every row starts in - stored as \"no selection\", which those columns could not tell apart from \"not running\". So the row went straight back to showing a duration, as though the button had never been pressed.\n\nIt now records that a job was queued, rather than inferring it. The row says so beside the button that started it instead of in a line above a scrolling list, and it says when the job finished.\n\n**A failure now says it failed.** It used to be swallowed: the recording kept the voiceprint it already had - which is right, a failed re-measure must not destroy a working one - but the row then recorded zero seconds of audio and looked exactly like a success.\n\nThe button is also renamed. **Re-measure this recording** is what it does; the person's voiceprint is the average of every recording behind it, so re-measuring one does not rebuild the whole thing.",
    added: [
      "A tick box on the Voiceprint tab to show only the recordings currently training the voiceprint, alongside the one for the recordings worth checking. It appears only when it would actually hide something.",
    ],
    changed: [
      "**Recompute voiceprint** is now **Re-measure this recording**, which is what it does. The person's voiceprint is the average of every recording behind it, so re-measuring one is not a rebuild of the whole print.",
    ],
    fixed: [
      "Pressing the button now says a re-measure is running, beside the button rather than in a line above a scrolling list - and said nothing at all before, whenever the whole speaker was selected.",
      "A re-measure that fails now says so. It used to leave the recording reporting zero seconds of training audio, which looked like a success.",
    ],
  },
  {
    version: "0.254.0",
    date: "2026-08-25",
    pr: 629,
    headline: "Fix a misattributed recording from the person's own card",
    summary:
      "A person's card lists every recording their voiceprint learned from, and now says whether each one sounds like the others. Until now, finding one that did not was where it stopped: if the recording turned out to be somebody else, you had to remember which meeting it was, open the transcript, find the speaker and fix it there.\n\nEach row now carries the same speaker picker the transcript and Speakers tab use, showing who it currently says this is. Change it to the right person, add someone who is not in the directory yet, mark it as **Multiple speakers** where two people were talking over each other, or unlink it entirely. Whichever you choose, the recording stops training that person's voiceprint the moment the transcript no longer says it is them.\n\nIt appears only on recordings **you own**. **Manage voiceprints** lets someone listen to a segment to judge whether it is the right person; editing somebody else's transcript is a different thing, and the server refuses it either way.",
    added: [
      "A speaker picker on every recording behind a person's voiceprint: reassign it to the right person, create someone not yet in the directory, mark it as **Multiple speakers**, or unlink it - without leaving the card. It is the same control the transcript and Speakers tab already use.",
    ],
    changed: [
      "The picker can now be given its own description for screen readers, because one person can appear as SPEAKER_00 in several different recordings and the label alone no longer identified which one you were changing.",
    ],
  },
  {
    version: "0.253.0",
    date: "2026-08-25",
    pr: 627,
    headline: "One list per person, and numbers that read the way they look",
    summary:
      "Each person's card had two tabs describing the same recordings in different words. **Voiceprint** listed them with the controls - play, untick, pick segments - and **Diagnostics** scored how well they resemble each other. Acting on a recording the second one flagged meant remembering its name, switching tabs and finding it again.\n\nThe wording made it worse. A header reading \"5 recordings resemble none of the others\" sat above a list whose rows said \"Matches the others\", because the header counted only the outliers while the list showed every recording. Both were true and together they read as a contradiction. The figures beside them were cosine distances printed under a label that reads as a match, so the worst recording in the whole directory displayed the largest and most reassuring number on the screen.\n\nThere is now **one list**. Every recording carries its own verdict in words, the ones worth listening to sort to the top, and a tick box narrows a long list to just those. The numbers are similarity, so high is good: the outlier that used to read \"closest other: 82%\" now reads \"closest match 18%\".\n\nIn the People directory the two warning panels are gone. They rendered full width above the list, and with both showing they pushed the person card almost off screen - the very thing they were asking you to look at. A person with something to say now carries a short amber line under their name, a **Needs review** filter narrows the directory to them, and the merge prompt appears beside the person you open. Either warning can be dismissed for the sitting.",
    added: [
      "A **Needs review** filter in the People directory, narrowing it to the people with a warning against them. Scanning a long directory for a colour is not a way to find anything.",
      "A tick box on the Voiceprint tab to show only the recordings worth checking, for someone who appears in a dozen meetings.",
    ],
    changed: [
      "The Diagnostics tab is gone; its verdicts are on the recordings themselves, in the one list that also has the controls. Recordings that sound unlike the rest sort to the top.",
      "The figures beside each recording are now **similarity** rather than distance, so a high number means a good match. They read the opposite way before.",
      "The two warning panels in the People directory became a line on the person's own row, so they no longer cover the card underneath. The merge prompt now appears beside the person you open.",
    ],
    fixed: [
      "The header above the recordings behind a voiceprint describes that list, so a count and the rows beneath it can no longer contradict each other.",
      "**Play voice** is hidden while the segments are not loaded instead of greyed out, which read as broken rather than as not yet applicable.",
      "Collapsing the segment list now stops any clip that is playing. It used to carry on with the Stop button no longer on screen.",
    ],
  },
  {
    version: "0.252.1",
    date: "2026-08-25",
    pr: 625,
    headline: "A voice stops training someone's voiceprint once you say it was not them",
    summary:
      "Unassigning a speaker, or handing them to a different person, moved the label on the transcript and left the voiceprint alone. The recording carried on teaching the original person's voice indefinitely - and it did not appear on their Voiceprint tab, because that tab lists the speakers currently linked to them. There was no way to see it and no way to remove it.\n\nOn this platform six recordings were in that state. Three of them were training one person's voiceprint using audio that had since been labelled as somebody else, so both people were being taught the same voice.\n\nThis was also why the Diagnostics tab could not be acted on. It scores samples, the Voiceprint tab lists linked speakers, and the two were reading different sets - so the worst-scoring recording on the most-flagged person had no row anywhere to play, untick or reassign. It was not a missing button.\n\nThe rule is now a single one, applied everywhere a voiceprint is worked out: a recording trains someone only while the transcript still says that speaker is them. The affected voiceprints are rebuilt when the server next starts, and the recordings behind them stay listed, marked **No longer linked to this person**, so nothing disappears without saying so.",
    fixed: [
      "A recording stops training someone's voiceprint as soon as its speaker is unassigned or handed to a different person. Previously it kept contributing forever, invisibly - six recordings on this platform were doing so, three of them teaching one person a voice already labelled as someone else.",
      "The Diagnostics tab and the Voiceprint tab now describe the same set of recordings. They did not, which is why a recording flagged as sounding wrong could have no row to play, untick or reassign.",
      "Voiceprints built from those recordings are rebuilt automatically when the server starts, so a stored voice that was averaged with the wrong person's corrects itself.",
      "The health ranking no longer flags someone as scattered on the strength of recordings that are not training them, which sent you to review a problem that did not exist.",
    ],
  },
  {
    version: "0.252.0",
    date: "2026-08-25",
    pr: 620,
    headline: "See which recordings behind a voiceprint do not sound like the others",
    summary:
      "A voiceprint learns from several recordings, and until now nothing showed whether those recordings actually sound like each other. They often do not. Measured across this platform, of the samples belonging to people enrolled more than once, **a third resemble none of their others** - and the widest pair inside a single person is almost completely dissimilar, which two recordings of one human cannot be.\n\nThere are two explanations and they need opposite responses. It might be the same voice somewhere new - a phone, a car, a meeting-room speaker - which is exactly the audio a voiceprint benefits from having. Or it might be **someone else enrolled under that name**, which is why recognition drifts. The numbers cannot tell them apart. You can, by listening.\n\nSo each person's card gains a **Diagnostics** tab saying, in words rather than distances, which of their recordings match the others and which resemble nothing - and the People directory now leads with the handful worth checking, so finding them does not mean opening every card. From there the existing controls do the rest: play the recording to hear who it actually is, and untick it if it is not them.\n\nNothing about recognition changes in this release. This is the look before the fix: knowing which samples do not belong matters before anything starts treating each of them as a voice in its own right.",
    added: [
      "A **Diagnostics** tab on each person's card: which of the recordings training their voiceprint resemble the others, which look like a different recording condition, and which resemble nothing at all. Two figures per recording - how close its nearest neighbour is, and how it compares with the rest taken together - because those disagree exactly when a pair sits together but away from everything else.",
      "The People directory leads with the voiceprints worth checking, worst first, each opening straight into that person's diagnostics. With a directory of any size, knowing which people to look at is most of the work.",
    ],
  },
  {
    version: "0.251.0",
    date: "2026-08-25",
    pr: 619,
    headline: "Diariz asks when it is not sure, and can go back over recordings it never checked",
    summary:
      "Diariz recognised a voice, was almost sure, and said nothing. That was the whole shape of the problem: identification ran with a single strict cut-off, so a match either cleared it silently or vanished. On the measured instance that left 38 speakers sitting inside the acceptance distance, unnamed, because identification only ever runs at the moment a recording is transcribed - enrolling someone afterwards never went back.\n\nThere is now a middle answer. A match that is close but not certain becomes a question rather than a silence: **Might be Ada Lovelace - is it?** appears on the speaker in the transcript, where the words and the audio already are, and the same questions gather in **Voices to confirm** in the account menu when you would rather work through a backlog. Confirming one teaches that voice; declining is remembered, so the same pair is never offered again.\n\nA **re-scan** applies the current settings to everything already transcribed, which is what collects the matches that were missed. It previews first - \"this would name 38 and ask about 90\" - before you commit, and it only ever adds a name. It will not take one away, whatever you change.\n\nThe four settings behind all this are now editable in Settings rather than fixed at deploy: how close a match must be to be applied, how close to be asked about, how far it must beat the next person, and how little speech is too little to judge. **Your current behaviour is unchanged** - the defaults are the values the platform was already running.",
    added: [
      "A voice match that is close but not certain is now offered for confirmation instead of discarded - on the speaker in the transcript, and gathered in **Voices to confirm** in the account menu. Confirming teaches the voiceprint; declining is remembered so the same pair is never suggested again.",
      "A re-scan that applies the current identification settings to recordings already transcribed, recovering matches that were missed because the person was enrolled later. It previews what it would do before applying, and never removes a name.",
      "Voice identification settings a Platform Administrator can change without a redeploy: the acceptance distance, the ask-about distance, how far the best match must beat the runner-up, and the minimum speech before a voice is matched at all.",
    ],
    changed: [
      "A speaker with very little speech is no longer matched against voiceprints. A second and a half of audio produces a confident-looking number that is not worth trusting, and it would go on to train whatever it matched. The floor is three seconds by default, and adjustable.",
      "The identification threshold moved from an environment variable into Settings. A deployment that had set `IDENTIFICATION_THRESHOLD` should check the value in Settings after upgrading - the shipped default matches what deployments were already running.",
    ],
    fixed: [
      "Assigning a speaker to a person quietly pulled every voice sample that had been dropped from their training back into their voiceprint, undoing the exclusion with nothing to show it had happened.",
    ],
  },
  {
    version: "0.250.2",
    date: "2026-08-25",
    pr: 618,
    headline: "All-day entries stay on the one day they are actually on",
    summary:
      "An all-day entry - a holiday, a birthday, someone's out-of-office day - was drawn on two days: the day it is on, and the day after. It also inflated the following day's event count in the day header. Anyone in the UK saw this from late March to late October and not at all in winter, which is the clue to what it was.\n\nA date-only entry names calendar dates. It has no time and no timezone, but it still has to travel to your browser as a moment in time, and each calendar source picked its own moment to send. Google and Outlook used whatever timezone the server happens to run in; subscribed .ics feeds used UTC. Your browser then turned that moment back into local time - and during British Summer Time midnight UTC is one o'clock in the morning here, so a Monday entry ran from 01:00 Monday to 01:00 Tuesday and was counted as touching both days.\n\nBoth ends are now fixed. The server sends every date-only entry the same way whatever machine it runs on, and the calendar places one by the dates it names rather than by converting a moment in time, so it cannot drift again.",
    fixed: [
      "An all-day entry from any calendar - Outlook, Google or a subscribed .ics feed - appears only on the day it is on, instead of also appearing on the day after throughout British Summer Time.",
      "The day header no longer counts an all-day entry against the following day.",
      "A multi-day all-day entry covers exactly the days it names, no more.",
    ],
  },
  {
    version: "0.250.1",
    date: "2026-08-25",
    pr: 617,
    headline: "A rescheduled meeting no longer disappears from the calendar",
    summary:
      "If you recorded a meeting and it was later moved to another day, that meeting stopped being drawn on the Calendar tab - not on the day it had moved to, and not on the day it left. The only trace was the recording sitting on the original day, and the day header, which kept counting the meeting it was not showing: \"10 events\" over nine blocks.\n\nThe cause was the rule that stops a meeting being drawn twice. When a recording is linked to a meeting, the recording's block stands in for both, so the separate meeting block is dropped. That was being decided from every recording you have rather than from the ones on the day being drawn - so a link that crossed a day boundary suppressed the meeting on one day while its recording stood in for it on another, and nothing was drawn anywhere. Outlook keeps the same identifier for a meeting when it is rescheduled, which is exactly how a link comes to cross days without anyone doing anything unusual; linking a recording to a meeting on another day by hand did it too.\n\nBoth are now drawn where they actually are: the meeting on its day, the recording on its own, still marked as linked.",
    fixed: [
      "A meeting moved to a different day after you recorded it is drawn on the day it moved to. Previously it vanished from the calendar entirely.",
      "The same applies to a recording linked by hand to a meeting on another day - the meeting stays on its own day rather than being swallowed by the link.",
      "The day header's event count and the blocks below it no longer disagree.",
    ],
  },
  {
    version: "0.250.0",
    date: "2026-08-25",
    pr: 610,
    headline: "Hear the voice behind a voiceprint, and see every recording it is used in",
    summary:
      "The Voiceprint tab used to list only the recordings you had enrolled by hand. That was a fraction of where a voiceprint is actually applied - automatic recognition links a speaker without creating a training sample - so the list read as an arbitrary handful with no way to tell why those recordings and not others. It now lists every recording the person appears in, says how each one came to be attributed to them (recognised automatically, or named by hand), and lets you tick any of them in or out of training. Adding one is instant: the voice was already measured when the recording was transcribed.\n\nYou can also now hear it. Every segment has a play button, and each one plays a short clip cut from the recording on the server rather than handing the browser the whole file - so judging whether a voice really is the right person no longer means opening the recording and hunting for the speaker.\n\nBecause a shared directory means someone's voice can appear in recordings you do not own, playback there is gated behind a new **Manage voiceprints** permission, held by platform administrators. It is deliberately narrow: it plays only the spans that person actually spoke, never arbitrary offsets, and never the rest of the meeting's transcript. Every such access is logged. Ordinary directory work - merging duplicates, editing contact details - stays on **Manage people** and grants no audio at all.\n\nOne consequence worth knowing: dropping a recording from training no longer discards it. It is remembered as excluded, so the record that someone identified that speaker survives and re-including it is a single tick.",
    added: [
      "The Voiceprint tab lists every recording a person appears in, not only the ones enrolled by hand, with how each was attributed and how much they speak in it. Tick a recording to add it to the voiceprint or untick it to drop it - no re-transcribing needed either way.",
      "Play any segment of a person's speech from their card. Clips are cut on the server, so playback never pulls the whole recording.",
      "A new **Manage voiceprints** permission covers assessing and tuning voice recognition, including hearing a person's speech in recordings you do not own. Platform administrators have it; Administrators keep directory access without it.",
    ],
    changed: [
      "Removing a recording from a voiceprint now excludes it rather than deleting the record of it, so it can be put back with one tick and nothing can silently re-add it later.",
      "The tab reads a speaker's segments through a dedicated endpoint that returns only that speaker. Previously it read the whole transcript, which failed outright for a recording you do not own.",
    ],
  },
  {
    version: "0.249.0",
    date: "2026-08-24",
    pr: 608,
    headline: "See which account a person is, split a segment two people share, and choose what trains a voiceprint",
    summary:
      "Three things you could not see or change about the people in your meetings.\n\nTwo people with the same name were indistinguishable. The duplicates banner reported \"Same name: Ken Hayward, Ken Hayward\" and the merge dialog refused with \"these records each have a Diariz account\" - without naming either account, so there was no way to tell which record was you. Every list that can show two alike people now carries the account each one is, or says plainly that there is no account behind it.\n\nA transcript segment sometimes has two voices in it, one of them dominant. Naming the dominant speaker enrolled the interloper's audio along with theirs, because a voiceprint is built from whatever the segment covers. You can now split a segment at an exact word - the worker keeps word-level timings from this release onward - and hand the new part to whoever actually said it, or to a new speaker. The silence between the two words goes to neither side, which is the point: an estimated cut would slice the wrong audio.\n\nAnd nothing showed what a voiceprint had learned from. A person's card gains a Voiceprint tab listing the recordings behind it, and expanding one lets you untick the segments where someone else was talking over them. Ticking a few and pressing Recompute re-embeds from exactly that audio. The pooling cap has gone from 30 seconds to 120, so a hand-picked selection is actually used rather than silently truncated to its opening - and where the cap still bites, the tab says \"Using 2:00 of the 4:12 selected\" rather than implying it used it all.\n\nOne limitation worth knowing: word timings only exist for recordings transcribed from this release onward. On an older recording the Split control is shown disabled, saying that re-transcribing is what unlocks it.",
    added: [
      "Every list that can show two people of the same name - the directory, the possible-duplicates banner and the merge dialog - now shows which Diariz account each person is, marks your own, and says plainly when there is no account behind a record.",
      "Split a transcript segment at an exact word boundary and reassign the new part to another speaker, or to a new one. For the block that is mostly one person with someone else's few words inside it.",
      "A Voiceprint tab on each person's card: which recordings train their voiceprint, how much of each one's audio is behind it, and tick boxes to choose which segments to keep. Recompute re-embeds from exactly what you picked.",
      "A speaker whose audio was re-attributed is flagged as needing recomputing, so a voiceprint that no longer describes its own audio says so instead of quietly drifting.",
    ],
    changed: [
      "The worker now keeps WhisperX's word-level timings on each segment, which is what a split snaps to. Recordings transcribed before this release have none and cannot be split until they are re-transcribed - the control says so rather than disappearing.",
      "Voiceprint training pools up to 120 seconds of audio per speaker, raised from 30. Where the cap still applies, the amount actually used is stated against the amount selected.",
    ],
    fixed: [
      "Turning on automatic same-speaker merging destroyed the word timings behind a transcript, which would have made merged recordings unsplittable - invisibly, since the transcript reads identically either way.",
    ],
  },
];
