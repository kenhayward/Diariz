---
title: Merging two people
summary: Merging folds one directory record into another and deletes the one you merged away. The record you keep wins on every detail it already has, and picks up only what it was missing. There is no undo.
group: advanced
order: 26
---

The same person can end up in the directory twice - most often because one of the records came from
**enrolling a voice** and the other came from a **Diariz account**. An enrolled record starts with nothing but
a name, because that is all the speaker label told Diariz; an account record carries the name and email
address from the account. Both are the same human.

Diariz points these pairs out. It never merges them for you.

## Which record you keep matters

Merging is not symmetric. The record you keep is the one that survives, and it **wins on every detail it
already has**. The other record only fills in the blanks.

So if you keep a record with no job title, and merge in one that has "Director", the survivor becomes a
Director. But if the survivor already says "Presenter", it stays "Presenter" - the other value is not kept
anywhere.

The merge window shows which way round it is going and lets you swap it, along with a list of exactly what
will happen to these two records.

## What moves

- **Voice samples and the voiceprint.** Every sample behind the record being merged away moves to the
  survivor, and the voiceprint is recalculated from the combined set. This is usually the whole point: it is
  how a voice enrolment gets attached to the right person.
- **Contact details the survivor was missing** - job title, company, email address, phone number.
- **The Diariz account**, if the record being merged away had one and the survivor did not. The survivor
  becomes that account's person.
- **Speaker labels.** Anywhere in your recordings that was labelled with the record being merged away is
  relabelled with the survivor's name.

The record you merged away is then deleted. **This cannot be undone**, and because the directory is shared,
it changes what your colleagues see in their recordings too.

## Two accounts are never merged

If both records have a Diariz account, Diariz refuses. Two accounts are two people, however alike the names
look, and merging them would cut one of them off from their own record - including their ability to opt
themselves out of voice-printing. Rename one of the records instead, or delete it.

## If the pair is not really a duplicate

**Dismiss** hides that suggestion while you are in the directory, so the ones behind it are easier to work
through. It is not a permanent decision and nothing is recorded: reopen People and every suggestion is back.
That is deliberate, because a pair you dismiss today may be a genuine duplicate once someone fills in the
missing email address.

Diariz spots pairs by matching email address, or by matching name once spacing and capitals are ignored - so
two different people who genuinely share a name will keep being suggested, and can keep being dismissed.
