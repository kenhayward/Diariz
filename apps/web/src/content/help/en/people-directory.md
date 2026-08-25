---
title: The people directory
summary: Everyone who appears in your meetings, in one shared list. A person does not need a voiceprint, can carry contact details, and can be opted out of voice-printing entirely.
group: advanced
order: 25
---

**People** is the list of humans who turn up in your meetings. Open it from the account menu, or from
**Manage people** on a recording's Speakers tab.

A person has a name, and optionally a **job title**, **company**, **email address**, **phone number**, and
an **internal or external** marker. None of that is required: a name alone is a perfectly good record.

## A person does not need a voiceprint

This is the important part. You can add a client you have never recorded, so that their contact details are
ready when you do. Someone with no voiceprint simply will not be recognised by voice - Diariz will not
guess at them.

A voiceprint is added by enrolling someone from a recording's speaker, and can be removed again without
removing the person.

## Seeing what a voiceprint was trained on

Selecting someone opens their record: **Profile** for their details, and **Voiceprint** for the audio behind
their biometric.

The Voiceprint tab lists every recording this person appears in - not only the ones you enrolled by hand.
Each row says how they came to be attributed there (**Recognised automatically** or **Named by hand**), how
much they speak in it, and whether that recording trains the voiceprint. This is where to look when Diariz
starts naming the wrong person: recognition drifts when a voiceprint has learned from a speaker who was
misattributed, and that shows up here as a recording you would not expect.

**Trains the voiceprint** on any row adds or removes that whole recording. Adding is instant - the voice was
measured when the recording was transcribed, so nothing needs re-transcribing. Removing does not throw the
record away: Diariz remembers that you identified that speaker, so putting it back later is one tick.

A row marked **No longer linked to this person** is one where the speaker has since been unassigned, or given
to somebody else, on the transcript. It no longer trains the voiceprint - saying it was not them is enough -
but it stays listed so you can see what it used to contribute.

**Play segment** plays a short clip of just that line, so you can check by ear whether the voice really is
this person before deciding. If the person appears in a recording belonging to someone else, you will only
hear it if you have the **Manage voiceprints** permission - and even then you hear only what they said, never
the rest of the meeting. Without it the row still appears, marked as being in a recording you cannot access.

**The Diagnostics tab tells you which recordings to be suspicious of.** A voiceprint learns from several
recordings, and they do not always sound like each other. Each one is marked as matching the others, as
probably a different recording condition, or as resembling none of them.

That last one is not a verdict of wrong. A recording can sound unlike the rest for two opposite reasons: it is
the same person on a phone, in a car, or on a meeting-room speaker - which is useful audio to have - or it is
**somebody else** who was named as this person by mistake, which is why recognition starts drifting. Only
listening tells you which. Play it from the Voiceprint tab, and if it is not them, untick it.

The People list leads with the voiceprints worth checking, so you do not have to open everyone to find them.
If nothing is listed there, nothing needs attention.

**Show segments** on any of them lists what that speaker said, with a tick box per line. Untick the lines
where someone else was talking over them and press **Recompute voiceprint**; Diariz re-reads the audio you
left ticked and rebuilds the voiceprint from it. Tick as many as you like before pressing it - the work
happens once, not once per click.

A few things worth knowing:

- **Ticking everything means "the whole recording"**, not a snapshot of today's lines. That matters because
  re-transcribing splits the transcript up differently, and a fixed selection would then point at the wrong
  moments.
- **You cannot untick everything.** An empty selection would mean the whole recording again, which is the
  opposite of what it looks like it should do, so the button stays disabled until at least one line is
  ticked.
- **Recomputing takes a moment.** The row says "Recomputing..." while it runs. It shares the queue with
  transcription, so if a recording is being transcribed it will wait its turn.
- **Diariz uses up to two minutes of audio** per recording. Where you have selected more than that, the row
  says how much it actually used against how much you picked, rather than implying it used all of it.
- **"Needs recomputing"** on a recording means its audio was re-attributed - usually because a segment was
  moved to another speaker - so the stored voiceprint no longer matches. Nothing is recalculated on its own;
  press Recompute when you are ready.

## Telling two people of the same name apart

Under each name, the list shows **which Diariz account that person is** - the account's email address, with
your own marked "your account" - or says plainly that there is no account behind them. Two colleagues called
the same thing are otherwise identical rows, and this is what separates them.

## The directory is shared

One person is one record for the whole platform, however many colleagues have recorded them. That is what
makes an erasure request a single deletion rather than a hunt through everyone's private lists.

The trade-off is worth knowing: **a voiceprint someone else enrolled will also name that person in your
recordings.** There is no setting that changes this - it is what a shared directory means.

Because the list therefore contains every external contact the organisation has recorded, **browsing it
needs the Manage people permission**, as does editing, deleting or merging anyone other than yourself.
Finding someone in order to name a speaker in your own recording needs nothing.

## Opting out of voice-printing

Ticking **Opted out of voice-printing** destroys that person's voiceprint and every voice sample behind it,
and stops them being recognised from then on.

- Names Diariz applied **automatically** go back to the anonymous speaker label.
- Names **you typed** are kept, and stay attached to the person. Those are your record of who was in the
  room, not something worked out from their voice.
- **Unticking it does not bring the voiceprint back.** They would have to be enrolled again from a
  recording.

**You can always opt yourself out.** That never needs a permission - deciding whether your own voice is
held is yours.

If what you actually want is to remove someone entirely, delete the person instead. Opting out keeps the
record of who attended; deleting does not.

## Possible duplicates

Two colleagues who each enrolled the same client will produce two records. Diariz points these out when it
spots a shared email address, or the same name once spacing and capitals are ignored.

It only ever points them out. **Merging is always your decision**, because it deletes one of the two
records, cannot be undone, and in a shared directory affects everyone's recordings. Check the direction
before you merge: the person you are merging *into* is the one that survives.

Choosing **Review and merge** opens a window that spells out what would happen to those two records
specifically - which one survives, what moves across, and what is deleted - and lets you swap the direction
before committing. See [Merging two people](/help/merging-people).

## People with a Diariz account

Everyone with an account is automatically in the directory, and their name and email address come from
their account rather than being edited here - so those two fields are shown but locked. Everything else,
including their job title and company, is editable as normal.
