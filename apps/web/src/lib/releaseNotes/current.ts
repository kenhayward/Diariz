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
    version: "0.275.0",
    date: "2026-10-03",
    pr: 809,
    headline: "Administrators can switch live transcription off",
    summary:
      "Live transcription - the transcript that fills in on the notes panel while you record - now has an on/off switch for the whole platform, for when the server is overloaded and needs the pressure taken off. A Platform Administrator finds it at the top of the AI tab in Platform settings: Live transcription during recording.\n\nTurning it off takes effect within seconds. Every meeting in progress stops being transcribed live, work already waiting is dropped rather than worked through, and the memory the dedicated live transcription worker uses on the server is freed. Anyone recording sees the live transcript leave their notes panel, with a line saying it was turned off by an administrator and that their recording and its full transcript are unaffected. A recording started while it is off simply shows notes and captures, with no live transcript.\n\nTurning it back on applies to recordings started afterwards, so a meeting never picks up again part-way with a gap. The settings page shows since when it has been off.",
    added: [
      "A Platform settings switch to turn live transcription off for everyone, effective within seconds, freeing the live worker's memory",
      "The notes panel says when live transcription has been turned off, instead of the transcript silently disappearing",
    ],
  },
  {
    version: "0.274.2",
    date: "2026-10-03",
    pr: 808,
    headline: "A file chat cannot read now says so",
    summary:
      "Adding a file to a chat message could fail with a server error when the file was damaged or unusual - a corrupt or truncated PDF or Office file, or a spreadsheet or slide deck containing an empty sheet or slide, as some programs other than Office produce.\n\nA file that cannot be read now gets a clear message saying so, and a spreadsheet or deck with an empty sheet or slide is read normally, skipping just the empty one.",
    fixed: [
      "Adding a damaged PDF or Office file, or a spreadsheet or deck with an empty sheet or slide, to a chat message gave a server error (#807)",
    ],
  },
  {
    version: "0.274.1",
    date: "2026-10-03",
    pr: 806,
    headline: "Summaries read more of a long meeting, and live rows split at the right time",
    summary:
      "Two follow-ups to splitting rows where the speaker changes.\n\nSummaries, minutes, action items and tags are written from the transcript up to a fixed length, and every row used to repeat its speaker's name. Now that one person's turn can span several rows, consecutive rows by the same person are passed to the AI as one passage, so more of a long meeting fits before the cut-off. The transcript itself is unchanged.\n\nSplitting a row of the live transcript shown during a meeting put the two halves at the wrong time - seconds into that stretch of audio rather than where the row really was in the recording. The word timings on live rows are now in recording time, so a split lands where you clicked.",
    changed: [
      "Consecutive rows by the same person reach the AI as one passage, so summaries, minutes, actions and tags see more of a long meeting",
    ],
    fixed: [
      "Splitting a row of a live transcript gave the halves the wrong timestamps (#805)",
    ],
  },
  {
    version: "0.274.0",
    date: "2026-10-03",
    pr: 804,
    headline: "Words show under the person who said them",
    summary:
      "When someone spoke partway through a sentence - a quick \"yes\" while another person was talking, or an answer that began before the question had finished - the transcript used to show the whole passage under one name, so their words appeared under the wrong person.\n\nThe transcript now starts a new row wherever the speaker changes between words. On a public meeting benchmark this cuts the words shown under the wrong speaker by about a quarter (from 8.6% to 6.4%), a bigger gain than any change of speaker-recognition model we measured. A single stray word inside someone else's sentence is not split off, so rows only break where the speaker really changes. Voiceprints for recognising people are built from cleaner audio as a result.\n\nYou will see a few more, shorter rows, including one-word replies such as \"Yeah.\". Merge rows joins them back if you prefer. This applies to new recordings; re-transcribe a recording to apply it to an older one. The transcript shown during a live meeting is unchanged for now; the final transcript after you stop uses the new rule.",
    changed: [
      "A transcript row is split where the speaker changes between words, so an interjection appears under the person who said it (#803)",
      "Voiceprints are built from audio that belongs to one speaker more often",
    ],
  },
  {
    version: "0.273.6",
    date: "2026-10-02",
    pr: 802,
    headline: "A clean web test run again",
    summary:
      "Behind-the-scenes only. One web test printed two error messages on every run even though it passed, because its stand-in for the server returned nothing where the real server returns the new recording. It now returns a recording like the real one, so a passing run is silent again and a genuine error stands out.",
    fixed: [
      "A recorder test printed 'Attaching notes failed unexpectedly' errors on every passing run (#801)",
    ],
  },
  {
    version: "0.273.5",
    date: "2026-10-02",
    pr: 800,
    headline: "Error reporting keeps your details private on its new version",
    summary:
      "The web app's error reporting moves to the current major version of its library. Nothing changes in what it sends: errors still go only to the error tracker an administrator configures, without your IP address, cookies, request contents or other personal details.\n\nThe new version reversed its defaults and would collect all of those unless told not to, and quietly ignores the setting Diariz used to turn them off. Diariz now switches off each kind of personal data by name, so the upgrade keeps reports exactly as private as before.\n\nBehind the scenes, the web test tooling also moves to its next major version.",
    changed: [
      "Error reporting library updated to @sentry/react 11, with every kind of personal data collection switched off explicitly",
      "Web test tooling updated to vitest 5, and jsdom is no longer held back (#791)",
    ],
  },
  {
    version: "0.273.4",
    date: "2026-10-02",
    pr: 799,
    headline: "Security fixes for libraries held back upstream",
    summary:
      "Clears the remaining open security alerts. In each case another library had locked in an older, vulnerable version of something it uses, so the routine updates could not reach it. Diariz now forces the fixed versions directly.\n\nThe API reference page no longer offers Scalar's built-in AI chat assistant. Diariz never used it, it talks to Scalar's own hosted service, and it was where one of these libraries ran in your browser. The reference itself works exactly as before.\n\nThe other affected libraries are only used to build and test Diariz and its n8n node, and never reached users.",
    changed: [
      "The API reference page no longer shows Scalar's AI chat assistant",
    ],
    fixed: [
      "Vulnerable versions of undici, @ai-sdk/provider-utils, axios and brace-expansion were locked in by other libraries (#798)",
    ],
  },
  {
    version: "0.273.3",
    date: "2026-10-01",
    pr: 793,
    headline: "Dependency updates",
    summary:
      "A routine round of library updates across the web app, desktop app, server and transcription worker. Nothing changes in how Diariz works.\n\nThe server picks up a security fix in OpenIddict, the library behind sign-in for connected apps such as the Claude connector, which tightens how it checks who a client request is meant for. The web app gets the current React, editor and sanitiser releases, and the desktop app moves to the latest Electron 44 patch, which arrives with the next desktop release.\n\nThe transcription worker was checked on a GPU with the newest speaker-separation release, and its storage library was checked against the SeaweedFS store that now holds recordings.",
    changed: [
      "Server libraries updated, including the S3 client, OpenIddict, Redis, Sentry, MailKit and SkiaSharp",
      "Web libraries updated, including React 19.3, the Tiptap editor, DOMPurify, markdown-it, i18next and React Router",
      "Desktop shell updated to Electron 44.4.5, plus js-yaml, fast-uri, brace-expansion and undici",
      "Transcription worker requires pyannote.audio 4.0.7 or newer, and updates boto3 and the Sentry SDK",
      "CodeQL scanning action updated",
      "Dependabot no longer proposes major @types/node updates, which follow the Node version CI runs, and holds jsdom below 30.1 until the vitest 5 upgrade (#791)",
    ],
  },
  {
    version: "0.273.2",
    date: "2026-10-01",
    pr: 790,
    headline: "Sign-in works straight after a restore, and the worker shares the GPU",
    summary:
      "Two fixes found while moving the production server.\n\nAfter a platform restore, nobody could sign in - every login failed with a server error until the API was restarted, even though the restore said no restart was needed. The restore now refreshes the server's view of the database itself, so the instance is usable as soon as the restore finishes.\n\nThe transcription worker kept the GPU memory from its busiest job for as long as it ran - around 6 GB more than its models need. On a server where a local AI model shares the graphics card, that left the model short of memory and slowed summaries and chat. The worker now hands that memory back after every job and keeps only its models loaded.",
    fixed: [
      "Sign-in failed with a server error after a restore until the API was restarted (#783)",
      "The transcription worker held its peak GPU memory between jobs, starving a local AI model on the same card (#782)",
    ],
  },
  {
    version: "0.273.1",
    date: "2026-10-01",
    pr: 770,
    headline: "Object storage moves from MinIO to SeaweedFS",
    summary:
      "Nothing changes for people using Diariz: recordings, attachments and screenshots behave exactly as before.\n\nFor administrators: MinIO's free edition is no longer maintained and its images can no longer be downloaded, so a fresh install could not start. Audio and uploaded files are now stored in SeaweedFS, an actively maintained S3-compatible store, with the same bucket and file layout. Storage keys are set with S3_* settings (the old MINIO_* names still work) and generated with NewS3Keys.cmd; each key can reach only its own bucket. An existing server moves its data with a platform backup and restore rather than a bucket copy - follow docs/Server_Migration_Runbook.md, and do not simply pull and redeploy a running server, which would start on an empty store. A restore now reports how many files and bytes it put back, so the result can be checked against the backup.",
    added: [
      "deploy/NewS3Keys.cmd and new-s3-keys.sh generate storage keys for the server, the app and GlitchTip",
      "A restore reports objectsRestored and bytesRestored",
      "docs/Server_Migration_Runbook.md - moving to a new server by backup and restore",
    ],
    changed: [
      "The object store is SeaweedFS instead of MinIO. Settings are S3_ROOT_*, S3_APP_* and S3_BIND; the old MINIO_* names still work",
      "The app's own storage key is required; it no longer falls back to the administrator key",
      "The GlitchTip overlay is switched on with COMPOSE_FILE in .env, creates its own bucket, and gets a key that can reach only that bucket",
      "BringUpProd.cmd refuses to run when GlitchTip is configured but its overlay is not switched on",
      "The MinIO provisioning scripts (ProvisionDiarizMinio.cmd, ProvisionGlitchTipMinio.cmd and their shell versions) are removed - SeaweedFS reads its keys from the compose file",
    ],
    fixed: [
      "Fresh installs failed because the MinIO image could not be downloaded (#769)",
      "A web test about the nginx configuration failed on Windows checkouts while passing in CI (#781)",
    ],
  },
  {
    version: "0.273.0",
    date: "2026-09-16",
    pr: 767,
    headline: "Stronger defaults for sign-in, tokens and deployment",
    summary:
      "A round of hardening across sign-in, connected apps and the deployment itself.\n\nSign-in now pauses an account for fifteen minutes after ten wrong passwords in a row, and sign-in and connection requests are limited per address. If you hit a limit, the login page says so and asks you to wait a minute rather than reporting a wrong password.\n\nPersonal API tokens, MCP tokens and connections made through the Claude connector now stop working as soon as an administrator disables the account, and start again if it is re-enabled.\n\nFor administrators running the stack: the server now checks its configuration when it starts and refuses to run on placeholder or too-short secrets, and the compose file requires the secrets it needs rather than falling back to defaults. Redis now requires a password, and the API, Postgres and MinIO ports are reachable only from the host unless you choose otherwise. A new script creates a MinIO account for Diariz that can only reach its own bucket. Read the upgrade notes before deploying: several .env values are now required, and the whole stack needs recreating once.",
    added: [
      "Sign-in pauses an account for 15 minutes after 10 wrong passwords in a row",
      "Per-address limits on sign-in, account setup and connector authorization requests, with a clear message on the login page",
      "deploy/ProvisionDiarizMinio.cmd and provision-diariz-minio.sh create a MinIO account scoped to the recordings bucket (MINIO_APP_ACCESS_KEY / MINIO_APP_SECRET_KEY)",
      "API_BIND, MINIO_BIND and WEB_BIND choose which host addresses the published ports listen on",
    ],
    changed: [
      "Personal API tokens, MCP tokens and Claude connector sessions require the account to be enabled and active",
      "The API refuses to start outside Development when its signing key, worker secret or storage credentials are missing, too short or still placeholders, when no key storage path is set, or when the public URL is not https for the connector",
      "Required .env values: JWT_KEY, CALLBACK_SECRET, POSTGRES_PASSWORD, REDIS_PASSWORD, MINIO_ROOT_USER, MINIO_ROOT_PASSWORD and APP_PUBLIC_URL (and GLITCHTIP_ALLOWED_HOSTS with the observability overlay)",
      "Redis requires a password for every client, including GlitchTip",
      "The API (8080), Postgres (5433) and MinIO (9002) ports listen on 127.0.0.1 by default",
      "Endpoints require a signed-in user unless they are explicitly public",
      "LLM endpoints cannot point at link-local or cloud metadata addresses, or at the server itself",
      "The web server's access log no longer records query strings, and pages send no Referer",
      "SENTRY_ENVIRONMENT defaults to production",
    ],
  },
  {
    version: "0.272.0",
    date: "2026-09-16",
    pr: 764,
    headline: "Record actions during the meeting",
    summary:
      "The live notes panel can now record actions as the meeting goes. Switch the composer to Action (or press Alt+A), or turn any note into an action. Actions you record are pinned straight away, so they appear in the Actions tab as soon as the recording uploads, owned by you unless you change it. Automatic extraction still runs afterwards and adds what it finds alongside them, skipping repeats. Re-extracting a meeting's actions now keeps pinned actions and replaces only the rest.",
    added: [
      "Action toggle in the live notes composer (Alt+A), with an Actions filter chip",
      "Make action / Make note on lines in the live notes panel, including the pop-out window",
      "Owner (defaults to you) and due date on actions recorded live",
      "API: POST /api/recordings/{id}/actions/live",
    ],
    changed: [
      "Automatic action extraction adds to actions recorded during the meeting instead of being skipped",
      "Re-extracting actions keeps pinned actions",
    ],
    fixed: [
      "The MinIO image now comes from quay.io, pinned to the same build as before: Docker Hub stopped serving minio/minio, which broke integration tests and fresh deployments",
    ],
  },
  {
    version: "0.271.3",
    date: "2026-09-04",
    pr: 761,
    headline: "The live transcript stops losing pieces of the meeting",
    summary:
      "Since the live transcript started working in short pieces, a large share of them were being lost or quietly truncated. Roughly one in five failed outright and left a visible gap; many of the rest arrived with most of their audio missing and what remained stamped at the wrong time.\n\nThe cause was in how the pieces are stitched together before transcription. Audio arriving from the browser is cut at points that are not self-contained, and only the very first piece carries the description a decoder needs. Diariz already put that description back, but the pieces also need a wrapper around them that the browser only emits about every thirty seconds - so a six-second piece usually had none. Without it the audio either would not open at all, or opened part-way through and everything before that point was thrown away silently. Longer pieces used to include one of those wrappers by luck, which is why this appeared when the pieces got shorter.\n\nDiariz now supplies the wrapper itself. Measured against real recordings, every window that previously failed or arrived truncated now decodes complete and on time.\n\nSeparately, pressing Stop no longer produces a burst of failures. Work still in progress at that moment was looking for audio that had just been merged into the finished recording; that is a normal end to a meeting, not a fault, and it is no longer reported as one.",
    fixed: [
      "Live transcript pieces failed at about one in five, leaving gaps in the running text. The audio handed to the transcriber was missing a structural element the browser only emits every thirty seconds or so, and would not open without it.",
      "Live transcript pieces that did not fail were often truncated, losing most of their audio and mis-stamping the rest - which read as text going missing and timings drifting rather than as an error.",
      "Pressing Stop no longer logs a burst of failed pieces, or briefly reports the live transcript as degraded after the meeting has already ended.",
    ],
  },
  {
    version: "0.271.2",
    date: "2026-09-04",
    pr: 760,
    headline: "Live transcript paused now means paused, not stopped",
    summary:
      "Once the live transcript said \"Live transcript paused\", it never came back for the rest of that recording - however well everything was running by then. The message and the help both promised it would resume on its own once the transcriber caught up. It could not.\n\nThe pause is meant for a transcriber that has fallen behind: Diariz stops sending it more work until it catches up. It decided how far behind things were by looking at the oldest piece of audio that had not come back yet - but a piece it had just declined to send was never going to come back either, so it became the new oldest, and refused the next one, and so on to the end of the meeting. A single piece of audio that failed to transcribe was enough to start it.\n\nA piece of audio now stops counting the moment the live pass is finished with it, whether it was transcribed, failed, or was never sent. So a failure costs one gap in the running text - which is all it was ever meant to cost - and a genuine backlog pauses and then resumes, as described.",
    fixed: [
      "\"Live transcript paused\" never cleared once shown. A single failed piece of audio would stop the live transcript for the remainder of the recording, even though the recording, the finished transcript and everything else were unaffected.",
    ],
  },
  {
    version: "0.271.1",
    date: "2026-09-03",
    pr: 756,
    headline: "The live transcript can stop waiting behind a finished meeting",
    summary:
      "Live transcript work already jumps the queue ahead of finished recordings waiting to be transcribed. What it could not jump was a recording already being transcribed - and a long one takes over a minute, during which the live text of a meeting happening right now visibly stalls and then arrives in a rush.\n\nAn administrator can now run a second transcription service that handles nothing but live meetings, so the two never compete. Whichever service is free picks the work up, and one busy with an hour-long recording is simply not looking. It is off by default and turned on with a single command, because it holds a second copy of the transcription models in graphics memory - free on a card with room to spare, and not something to switch on blindly on a small one.\n\nNothing changes for anyone if it is left off.",
    added: [
      "An optional second transcription service dedicated to live meetings, so live text never waits behind a recording already being transcribed. Off by default; see the deployment guide.",
    ],
  },
  {
    version: "0.271.0",
    date: "2026-09-03",
    pr: 755,
    headline: "The live transcript keeps much closer to the room",
    summary:
      "Live transcript lines used to arrive up to about a minute after they were said. Most of that was not the transcriber working - it was audio waiting. Diariz sends your meeting up in pieces, and a piece could run up to 45 seconds before it was sent at all, so a sentence spoken at the start of one sat in your browser for three quarters of a minute before anything else could begin.\n\nThose pieces are now much shorter - 6 to 12 seconds instead of 20 to 45 - which takes the worst case from roughly 50 seconds down to under 20. The machine doing the transcribing was never the bottleneck: on the measured figures it was busy under a tenth of the time.\n\nThere is a real trade for this, and it shows up in speaker names rather than in words. Working out who is speaking needs a stretch of audio to compare voices across, and shorter pieces give it less to go on - so early in a meeting a voice may be split in two and joined up a little later, which you will see happen in front of you. The words themselves are unaffected, and the finished transcript after you stop is unchanged.\n\nThe lengths are now a server setting rather than something built into the app, so they can be tuned against real meetings without shipping a new version.",
    changed: [
      "Live audio is sent up in 6-12 second pieces rather than 20-45, so transcript lines appear far sooner after they are spoken.",
      "Speaker names may be corrected more often early in a meeting, which is the cost of the shorter pieces - a voice split in two is rejoined once there is enough of it to be sure.",
      "The piece length is now a server setting, so it can be tuned without a new release.",
    ],
  },
  {
    version: "0.270.4",
    date: "2026-09-03",
    pr: 754,
    headline: "A long meeting no longer gets heavier as it goes",
    summary:
      "Live transcription was doing work proportional to the whole meeting every time a new piece of transcript arrived, on both sides. The server renumbered every line it had ever written; the browser downloaded the entire recording - speakers, action items, the calendar link, the summary, the minutes - to draw a handful of new lines. Neither showed up on a short meeting, and both got steadily heavier on a long one.\n\nBoth now do work proportional to what has just arrived instead. Nothing about the transcript looks or behaves differently; it simply stops costing more as the meeting runs on.\n\nThis is groundwork. It is what makes it safe to send transcript up in much smaller pieces, which is the change that will actually bring the live text closer to the moment it was said.",
    fixed: [
      "Live transcription renumbered every line of the transcript each time a new piece arrived, and the browser refetched the entire recording to render it - so a ninety-minute meeting cost far more per update than a five-minute one.",
    ],
  },
  {
    version: "0.270.3",
    date: "2026-09-03",
    pr: 752,
    headline: "Live transcript lines are stamped when they were actually said",
    summary:
      "Lines in the live transcript could be stamped well before the moment they were spoken - by around half a minute in a meeting where it was noticed, with a screen capture taken at the same moment stamped correctly. The two sit on one timeline in the notes panel, so they visibly disagreed.\n\nThe live transcript is written a chunk at a time, and each chunk is transcribed with the previous one's audio in front of it so the model does not start mid-sentence. To place the result back on the recording's clock, Diariz has to subtract however much audio it put in front. It was subtracting the length your browser reported for that previous chunk, rather than the length of the audio actually prepended - two different measurements that had no reason to agree, and every millisecond between them landed on every line in the chunk.\n\nIt now measures the audio it prepends. Where the two agreed nothing changes; where they did not, the lines move to where they belong. The finished transcript that arrives after you stop was never affected - it is transcribed in one pass over the whole recording - so this only ever concerned the live text.",
    fixed: [
      "Live transcript lines could be stamped noticeably earlier than they were said, putting them out of step with screen captures on the same timeline. The offset is now measured from the audio rather than taken from a timing reported alongside it.",
    ],
  },
  {
    version: "0.270.2",
    date: "2026-09-03",
    pr: 751,
    headline: "The live meeting leaves the chat when the meeting does",
    summary:
      "Two corrections to the live notes panel, both from using it in a real meeting.\n\nThe **Live meeting** pill in the chat now disappears when you press Stop. It used to stay, on the theory that \"summarise the meeting I just had\" is the natural next question - but a pill labelled Live outliving the thing it names just reads as stale, and a finished recording can be asked about the ordinary way by opening it. Stopping one recording only clears its own pill, so starting a second meeting and stopping the first in the wrong order cannot take the wrong one away.\n\nThe **drag to chat** handle has gone from captures in the live notes panel. It could not work: the notes panel sits over the chat composer and holds focus, so there was nowhere for the drag to land. The **Chat** button on the same thumbnail does the same job and does it reliably. Dragging a thumbnail from a finished recording's Notes tab is unaffected - there the composer and the thumbnail are both on the page, and it works.",
    fixed: [
      "The Live meeting pill stayed in the chat after the recording was stopped, still calling a finished meeting live.",
    ],
    changed: [
      "Captures in the live notes panel no longer offer a drag-to-chat handle, which could not complete - the panel covers the chat composer. The Chat button on the capture is unchanged.",
    ],
  },
  {
    version: "0.270.1",
    date: "2026-09-03",
    pr: 748,
    headline: "The separate notes window stops being sent the same thing over and over",
    summary:
      "While a recording was running with the notes popped out into their own window, the main window was sending that window a complete copy of everything - every note, and a thumbnail of every screen capture you had taken - four times a second, for the whole meeting.\n\nIt was invisible: the notes window showed the right thing, and nothing looked slow. But a long meeting with a lot of captures meant the same pile of images being packed up and handed over 240 times a minute to say nothing had changed. On a machine already busy recording, encoding and streaming audio, that is work worth not doing.\n\nThe window is now told something only when there is something to tell it - a note filed, a capture taken, the recording paused, a new piece of transcript. Its clock is unaffected: it has been running on its own since the window gained one, which is exactly why the constant updates were unnecessary.",
    fixed: [
      "The separate notes window was sent the entire notes state - including a thumbnail of every screen capture - four times a second for the length of the recording, rather than when something it shows had actually changed.",
    ],
  },
  {
    version: "0.270.0",
    date: "2026-09-03",
    pr: 746,
    headline: "Take a note without leaving the call",
    summary:
      "The desktop app now holds two more global hotkeys while a recording is running, so a note or a question can be filed with the call still in front of you: **Ctrl+Shift+0** puts your cursor in the note box wherever you are, and **Ctrl+Shift+8** sends the running meeting to the chat. The existing capture hotkey is unchanged.\n\nThey are numbers rather than the obvious letters on purpose. A global shortcut is held for the whole meeting, and Ctrl+Shift+N is Chrome's incognito window and File Explorer's new folder, while Ctrl+Shift+C is copy in Windows Terminal and in VS Code. Taking either of those away for an hour would be a worse surprise than learning an unfamiliar number, so the two sit beside the capture hotkey on the number row. The panel prints the keys actually registered, so if you change one the hint changes with it.\n\nThe note hotkey follows the notes. If the panel is in its separate window, that window is raised and focused; if it is not, the main window opens the panel for you.\n\nThe separate notes window also gains two controls of its own. **On top** can be turned off so the window drops behind your call and back again, and **Compact** shrinks it to just the note box - for a single screen where the call needs all the room, but you still want somewhere to type.",
    added: [
      "Two more global hotkeys while recording: Ctrl+Shift+0 focuses the note box wherever you are, Ctrl+Shift+8 sends the running meeting to the chat. On macOS, the Command equivalents.",
      "A hint line at the bottom of the notes panel showing the hotkeys as they are actually registered, so it stays right if you change one.",
      "**On top** and **Compact** buttons in the separate notes window - let it fall behind a call, or shrink it to just the note box.",
    ],
    changed: [
      "The separate notes window opens larger by default (420 by 740), and can be made smaller than before when a screen is tight.",
      "The screenshot hotkey is unchanged, including one you have set yourself.",
    ],
  },
  {
    version: "0.269.0",
    date: "2026-09-03",
    pr: 745,
    headline: "Send the meeting you are in, or a capture from it, straight to the chat",
    summary:
      "The live notes panel gains a **Use in chat** button and a **Chat** button on every screen capture, so you can ask the assistant about a meeting while you are still in it without leaving the panel or copying anything out.\n\nUse in chat does not paste your transcript into the prompt. It hands the chat the recording itself, so every question you ask is answered against the transcript as it stands at that moment rather than a snapshot that went stale the second the meeting carried on. The server already knows a recording that is still running is unfinished and says so to the model, which is why it will not report an argument still in progress as settled. The pill stays put once attached, so a follow-up question needs no second press - and it deliberately stays after you stop, because \"summarise the meeting I just had\" is usually the next thing you want.\n\nCaptures reach the chat the same way, either from the button on the thumbnail or by dragging the thumbnail into the chat prompt. For that to work at all, a capture taken while the recording is streaming is now uploaded as you take it instead of waiting for you to press Stop. Nothing is lost if that upload fails - the capture stays where it was and goes up with the rest at the end, exactly as before.",
    added: [
      "**Use in chat** in the live notes panel puts the meeting you are recording into the chat prompt as sticky context, and confirms in place without closing the panel or moving your cursor.",
      "A **Chat** button on every capture in the panel, and drag-to-chat from the thumbnail, so a slide can be asked about while it is still on screen.",
      "A **Live meeting** pill in the chat composer, removable, that rides every question until you take it off.",
    ],
    changed: [
      "Screen captures taken while a recording is streaming now upload as they are taken rather than waiting for Stop. A capture the server refuses is kept and sent with the rest at the end, as before.",
      "Deleting a capture that has already uploaded now removes the server's copy too.",
    ],
  },
  {
    version: "0.268.0",
    date: "2026-09-03",
    pr: 744,
    headline: "Notes, captures and the live transcript on one timeline",
    summary:
      "The notes panel you use while recording no longer has tabs. Your notes, your screen captures and the live transcript now share a single stamped stream, with the box you type into fixed at the bottom of it.\n\nThe tabs were asking the wrong thing of you. What actually happens in a meeting is that you hear a sentence and want to write about it, and the Transcript tab put a click between those two moments at exactly the point you had the least attention to spare - and once you had clicked, the notes you were writing were the thing you could no longer see. Everything is now one list in the order it happened, so a note sits directly under the sentence it was about and a capture sits where the slide went up.\n\nEvery transcript line carries the time it was said, and hovering one reveals a small plus. Pressing it pins the composer to that moment, so a thought you have forty seconds late is still filed forty seconds back rather than wherever the clock has got to. The pin releases itself once you press Enter, and clicking the pinned time releases it without filing anything.\n\nThe separate notes window gets all of this too, and its clock now runs on its own rather than waiting for the main window to tell it the time - which matters, because that window is hidden behind your call precisely when you are using it.",
    added: [
      "A single stream in the notes panel: notes, screen captures and live transcript lines interleaved in the order they happened, each with its own timestamp.",
      "Filter chips - Everything, Notes, Captures - with counts that always show the whole meeting rather than the filtered view.",
      "A plus button on any transcript line pins the composer to that moment, so a note can be filed against something said earlier.",
      "An elapsed clock in the panel header, and a stamp badge on the composer showing exactly when the next note will be filed.",
    ],
    changed: [
      "The Notes and Transcript tabs are gone from both the notes popover and the separate notes window.",
      "The status line under the transcript is now short enough to read at a glance; the full explanation of why live text is not final is on its tooltip.",
      "The separate notes window ticks its own clock from a reading the main window sends, so it keeps time even while the main window is hidden to the tray.",
    ],
  },
  {
    version: "0.267.0",
    date: "2026-09-02",
    pr: 743,
    headline: "Drawing a capture area takes the screenshot",
    summary:
      "Choosing a capture area in the notes panel now takes a screenshot the moment you finish drawing it.\n\nNobody drags a rectangle across their screen for its own sake - they do it because they want a picture of what is inside it. Until now that took two steps: draw the area, watch the overlay disappear with nothing to show for it, then find the capture button and press it. The second step was pure ceremony, and the pause in between was long enough to lose the slide you were aiming at.\n\nThe pick is now the request. Cancelling with Escape still captures nothing, and neither does an area chosen after the recording has ended - a shot you did not ask for is worse than one you have to ask for twice. This applies wherever you choose an area: the notes popover, the separate notes window, and the tray item.",
    changed: [
      "Finishing a capture-area selection now takes a screenshot immediately, instead of only setting the area for a later capture.",
      "The capture area button's hint says what it now does - a screenshot is taken as soon as you finish drawing.",
    ],
  },
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
