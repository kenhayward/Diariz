---
title: Transcripts and speakers
summary: Every recording is transcribed with word-level timestamps and split by speaker. You can rename speakers, edit the text, and always flip back to the model's original words.
group: getting-started
order: 40
---

Transcription runs on the server and takes a few minutes depending on the length of the audio and how
busy the worker is. The meeting's status shows the progress, and the page updates on its own when the
transcript is ready.

## The spoken language

Diariz works out what language a recording is in by listening to the start of it. That is usually right,
but it decides before it knows whether anyone is speaking yet, so a recording that opens with a few quiet
seconds can be read as a language nobody in the meeting spoke.

If a transcript comes back in the wrong language, **re-transcribe** it and set the **Spoken language** in
the dialog. If your recordings are nearly always in the same language, set it once instead: Preferences,
then Profile, then **Transcription language**. Every new recording follows that unless you say otherwise
on the recording itself. Leaving either on **Detect automatically** keeps the old behaviour.

Word-level timing is only available for some languages. In the others you still get the full transcript
with its speakers - the start and end of each segment is just slightly less precise.

## Speakers

The transcription step separates voices and labels them `SPEAKER_00`, `SPEAKER_01`, and so on. It does
not know who those people are until you tell it.

- Click the **speaker label at the start of any row** to name that voice, or use the **Speakers**
  panel, which lists each speaker with their segment count and total talk time.
- The Speakers panel can play or step through just one person's segments, which is the quickest way to
  work out who a voice belongs to.
- If two speakers were merged, or one person was split in two, **re-transcribe** with a minimum and
  maximum speaker hint.

Once you enrol a person, Diariz recognises their voice in later recordings automatically. Manage the
stored voiceprints, including merging duplicates and erasing them, from the **Voice Prints** tab in
Preferences.

**When Diariz is nearly sure, it asks.** If a voice is close to someone it knows but not close enough to be
certain, the speaker stays unnamed and a short question appears beside it: "Might be Ada Lovelace - is it?"
Answer **Yes** and the speaker is named and that voice is added to their voiceprint, so the same voice is
recognised outright next time. Answer **No** and the speaker stays anonymous; Diariz remembers, and will not
ask you about that pairing again.

The same questions are gathered under **Review Voice Matches** in the account menu, just below Preferences,
which is the easier place to work through several at once. It opens as a window over whatever you were
reading, so you keep your place. The voices waiting are listed on the left; choosing one shows what that
voice actually said on the right, a line per segment. Each line has a play button - press it to hear that
moment, press it again to stop - and **Play all** runs through them in turn, highlighting each one and
bringing it into view as it plays. The tick and cross beside the question answer for the whole voice, and
answering one moves you straight on to the next.

**You can also take single segments out.** Sometimes a single speaker is not a single person: two people
share a microphone, or they talk over each other, and the software puts it all under one label. Press the
cross on a segment and it leaves the list.

**That is not just tidying the list - it decides what the voiceprint learns from.** Whatever is still there
when you confirm is the audio Diariz measures the voice from, so a stretch that is somebody else is genuinely
kept out. Nothing is written while you are choosing: taking segments out changes nothing until you press
**Confirm this voice**, and if you decline or close the window your choices are discarded. **Restore** puts
back anything you removed by mistake.

There is no tick to go with it, because there would be nothing for it to do: a segment counts unless you
take it out. Instead the panel tells you what confirming will train from - **Confirming trains from 28 of 30
segments** - so you can see where you stand without marking anything.

**Confirming the voice is what makes those marks count.** There is no separate save: pressing **Confirm this
voice** answers the question and trains the voiceprint from whatever is still in the list. While anything is
excluded the panel tells you what that will be, so you can check before committing. Your marks stay with the
voice they belong to, so you can look at another voice and come back to find them as you left them - but they
last only while the window is open. Close it without confirming and nothing is kept, because nothing was
written.

Excluding a segment shapes the **voiceprint**, not the transcript. Those segments stay under the same speaker
and will carry that person's name once you confirm the voice. If a speaker really is two different people,
re-transcribe with a minimum and maximum speaker hint instead.

You only ever see suggestions for your own recordings, and no permission is needed: the person who can say
whether a voice belongs to someone is whoever was in the meeting. Nothing is named until you say so - a
pending question changes nothing about the transcript, the summary or a search.

**A voice is only offered while there is still audio to judge it by.** Recording audio is deleted once it
passes the retention period your administrator sets, and the transcript is kept. Since the only honest way to
answer is to listen, a suggestion whose audio has gone is not offered - not in this list, and not on the
transcript either. Nothing is decided on your behalf; the question is simply not put to you.

**Recordings made before someone was enrolled are not checked automatically.** Diariz matches voices while a
recording is being transcribed, so a person you add today was not known when last month's meeting was
processed. A Platform Administrator can run a re-scan from Settings to go back over everything with the
current settings; it shows what it would do before doing it, and it only adds names - it never removes one.

Once a speaker is identified, the Speakers tab shows who they are - their job title, their company, and
whether they are internal or external - so you can read the room without leaving the transcript. Hovering the
**Internal** or **External** marker shows the rest of their details, and clicking a speaker to read their
segments puts a contact card above them, where their email address and phone number are links you can act
on.

Most people in the directory have nothing but a name - enrolling a voice is all it takes to create one - so
both the tooltip and the card will often say **no contact details are recorded**, with a shortcut to add
them. That is the honest answer: Diariz knows who spoke, not how to reach them, until somebody says.

If you can manage people, a **pencil** on the speaker's row opens their details for editing without leaving
the transcript. Saving closes it and the panel updates straight away. It offers contact details only -
erasing a voiceprint and deleting a person live in **People**, where you can see who else they affect. It edits the same shared record the People directory does, so a correction made here is a
correction everywhere. Manage
those details in **People**, which opens over the transcript so you do not lose your place; see
[The people directory](/help/people-directory).

**People are shared across the whole platform.** One person is one record, however many colleagues have
recorded them - which is what makes an erasure request a single deletion. The trade-off is worth knowing:
a voiceprint someone else enrolled will also name that person in your recordings. Browsing the directory,
and editing or deleting anyone other than yourself, needs the **Manage people** permission. Naming a
speaker on your own recording does not.

**A person does not have to have a voiceprint.** They can be in the directory with nothing biometric held
for them at all, and they can be **opted out** of voice-printing entirely.

Opting someone out destroys the voiceprint they have, along with every voice sample behind it, and stops
them being recognised from then on. Names that Diariz applied automatically go back to the anonymous
speaker label. **Names you typed yourself are kept** - those are your record of who was in the room, not
something worked out from their voice. Turning the setting back off does not bring the voiceprint back:
they would have to be enrolled again from a recording.

**You can always opt yourself out.** That one never needs a permission - choosing whether your own voice
is held is yours to decide.

**Renaming a speaker survives re-transcription.** The name is stored against the meeting, not against
the transcript text, so re-running transcription does not undo your work.

## Editing the transcript

Edits are kept separately from what the model originally produced. A revised row is marked, and a
**Show original / Show revised** toggle flips the whole transcript, so you can always get back to the
model's words.

Other things the transcript toolbar does:

- **Select mode** ticks individual segments so you can play, edit, translate, or delete just those.
- **Merge** joins consecutive rows by the same speaker. Notes and screenshots act as boundaries, so
  text either side of them stays separate. Merging is permanent for that transcript. If you always end
  up doing it, turn on **Merge each speaker's turn into one block** in Preferences - Recordings, and
  every recording is merged for you as soon as it finishes transcribing. While that is on, transcribing
  a recording again produces a merged transcript too, so turn it off first if you want short rows back.
- **Split** divides one row in two, for the block where a second person got a few words in - see below.
- **Download or email** the formatted transcript, which carries the summary, minutes, and actions with it.

## When one row has two people in it

Diariz attributes a whole row to one speaker. Sometimes that is wrong in a specific way: the row is mostly
one person, with someone else's few words inside it. Naming the dominant speaker then teaches their
voiceprint from the other person's voice too.

Select the row and press **Split**. The row is laid out word by word with a scissors between each pair of
words; click one and Diariz shows you the two halves it will make. Choose who spoke the new part - any
speaker already in the recording, or **New speaker** for a voice that has no row of its own - and press
Split.

The cut lands exactly on a word, using timings Diariz recorded while transcribing, and the silence between
the two words goes to neither half. That is the whole point: a guessed cut would hand a slice of the wrong
person's voice to whichever half gets used for recognition.

Two limits:

- **Older recordings cannot be split.** Diariz only started keeping word timings recently, so on a recording
  made before then the Split button is disabled and says so. Re-transcribe it and splitting becomes
  available.
- **Splitting a row you have edited discards your edit.** Both halves come from the model's original words,
  because there is no sensible way to divide your own wording at a word the model chose. Diariz asks before
  doing it.

Splitting is permanent for that transcript, like merging. Re-transcribing regenerates the original rows.

## The conversation-flow player

Above the transcript, the recording is drawn left to right as speaker-coloured blocks sized by how long
each person talked, with silence left dark. It shows the shape of the meeting at a glance, and it
doubles as the scrubber: click or drag anywhere on it to seek.
