---
title: Search and tags
summary: Search your meetings from the box above the list, widen it to every room, and add your own tags to a meeting from the automatic suggestions Diariz offers, or type your own.
group: recordings
order: 40
---

## Searching

A search box sits above the meetings list. Typing takes the list over with results; clearing it drops
you back exactly where you were browsing.

By default it searches the **folder you are in**, and a chip tells you which. Each hit shows the
matching words in context and the folder the meeting lives in. Clicking a hit opens the transcript
**at that moment**. Folders whose names match appear too and take you straight there.

**Search everywhere**, next to the result count, widens the search to every room you can see. The chip
switches to *Everywhere*, results are grouped under the folder each meeting lives in, and **Section**,
**Date**, and **Speaker** chips narrow them further. Those chip options are built from the results you
actually got, so none of them lead to an empty list.

Scope and filters last only as long as the search. Clearing the box returns you to your folder.

## Searching by meaning

If an administrator has configured an embeddings endpoint, transcripts are indexed for **semantic
search**. Search, chat, and the chat tools then match on meaning as well as keywords, so a conceptual
question finds the right moment even when the words do not match. Without an embeddings endpoint,
search stays keyword-only.

## Tags

Tags are yours to add. Open a meeting and click the **Tags** pill on its summary card to type a tag, see
the ones you have already added, and review suggestions.

After transcription, the AI model proposes up to 12 weighted topics it thinks the meeting was actually
about (participant names and filler words are excluded), but these are only suggestions - shown in the
pill's popover with dashed borders. Click one to adopt it, or dismiss it if it is not useful. A dismissed
suggestion will not be offered again on that meeting, but the same word can still be suggested on others.

Only tags you have adopted - typed by hand or picked from a suggestion - count toward your library. The
left panel's **Tags** tab shows them as a weighted cloud, where font size scales with how central a topic
is across your library. Click a tag to list the meetings that carry it. The expand button opens the cloud
in a large modal, where picking a tag also filters the panel and picking a meeting opens it.

Re-transcribing a meeting refreshes its suggestions, but never touches a tag you have already adopted or
dismissed. Existing libraries get fresh suggestions automatically when a server-wide AI model is
configured - each one still needs to be adopted before it joins the cloud. Anyone who can see a shared
recording can tag it, not only its owner.
