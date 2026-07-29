---
title: Transcripts and speakers
summary: Every recording is transcribed with word-level timestamps and split by speaker. You can rename speakers, edit the text, and always flip back to the model's original words.
group: getting-started
order: 40
---

Transcription runs on the server and takes a few minutes depending on the length of the audio and how
busy the worker is. The meeting's status shows the progress, and the page updates on its own when the
transcript is ready.

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

Once a speaker is identified, the Speakers tab shows who they are - their job title, their company, and
whether they are internal or external - so you can read the room without leaving the transcript. Manage
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
  text either side of them stays separate.
- **Download or email** the formatted transcript, which carries the summary, minutes, and actions with it.

## The conversation-flow player

Above the transcript, the recording is drawn left to right as speaker-coloured blocks sized by how long
each person talked, with silence left dark. It shows the shape of the meeting at a glance, and it
doubles as the scrubber: click or drag anywhere on it to seek.
