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

Selecting someone opens their record under two tabs: **Profile** for their details, and **Voiceprint** for
the audio behind their biometric.

The Voiceprint tab lists every recording that trains it - which speaker it came from and how much of that
audio is being used. This is where to look when Diariz starts naming the wrong person: recognition drifts
when a voiceprint has learned from a speaker who was misattributed, and that shows up here as a recording
you would not expect.

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
