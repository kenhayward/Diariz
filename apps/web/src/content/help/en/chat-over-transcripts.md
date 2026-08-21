---
title: Chat over your meetings
summary: Ask questions about one transcript, a folder, or your whole library. The chat works out its context from what you are looking at, and answers link back to the transcript.
group: asking-questions
order: 10
---

Chat needs an AI model to be configured. See [AI and model settings](/help/ai-model-settings).

## Context is inferred, not picked

You do not choose what the chat can see from a list. The label above the input tells you what it is
about, and it updates as you move around:

- **Current Transcript** when you have a meeting open.
- **Current Folder** when you have a folder open. The folder's roll-up summary, minutes, and aggregated
  actions become the context.
- **Selected Transcripts** when you have ticked two or more in the list.
- **All meetings** searches your whole library on demand instead of pre-loading transcripts.

A context-usage dial shows how much of the model's context window you are using. It is blue while you
have plenty of room, turns orange once you are past halfway, and red past three quarters - at which point
a long question or a large attachment may not fit alongside everything already in the conversation.

## Choosing a model

If your administrator has made more than one model available for chat, the sparkle button beside the
context dial opens a list of them. Each one shows its name and, in brackets, how much context it can
hold.

You can switch model in the middle of a conversation. Everything said so far goes across with your next
question, so the new model carries on from where the last one left off. That makes it easy to try a
second opinion, or to move up to a model with more room when a long conversation starts to fill the
dial. The dial updates the moment you choose, so it always shows the window of the model that will
answer you next.

Your choice is remembered, and it is stored with a conversation when you save one, so reopening it puts
you back on the model it was using. If your administrator later stops offering that model, the
conversation falls back to the standard one rather than failing.

## Asking about a screenshot

If a meeting has screen captures, the Notes tab lists them in a Screenshots section. Drag one from there
into the chat box and it appears as a small thumbnail above where you type. Ask your question as usual
and the picture goes across with it, so you can ask what a diagram shows or have a slide read back to
you.

You can attach more than one, dropping them in one at a time, and each thumbnail has an X in its corner
to take it back out. They stay attached while you keep asking, so a follow-up question about the same
picture needs no second drag, and they are kept when you save the conversation. If a capture has been
deleted since, it is quietly left out when you reopen.

Not every model can read images. Your administrator marks the ones that can, and the model list shows
which those are. If you attach a screenshot while a model that cannot read images is selected, the Send
button will not go - "Select a vision model" appears beside it. Switch to one of the marked models and it
sends. It works this way so you never get a confident answer about a picture the model never saw.

Large captures are shrunk to fit within 1920 by 1080 before they are sent, keeping their shape. Models
read that size more reliably than a full 4K image. A capture already smaller than that is sent exactly as
it was taken. Very small text in a dense 4K screenshot may not survive the resize - if that happens, open
the capture in the viewer and read it there.

## What you can do

- **Stream replies** as they are generated.
- **Attach PDFs or text files** to a question.
- **Drag in a meeting screenshot** for a vision-capable model to read.
- **Save conversations** and come back to them.
- **Include attachments** to feed a meeting's attached documents to the model. Documents are read into
  text, and URLs are fetched behind safety guards.
- **Dictate** your question using the microphone button in the chat input.

Answers cite their sources, and clicking a citation opens that moment in the transcript.

## Commands

- `/formula <name>` runs a formula on the recording you have open. See [Formulas](/help/formulas).
- `/attach` saves the whole conversation as a Markdown attachment on the current transcript or folder.
- `/help` lists the available commands.
- `/context` shows what the chat can currently see.

## Chat tools

If you turn tools on in Preferences, the assistant can search your **whole** transcript library rather
than just the current context: who said a phrase, what a person said about a topic, when a topic was
discussed, how often something was mentioned, who attended, speaker talk time, and more. Answers come
back as When, Who, and What.

Two tools can act rather than just read:

- **Email you.** It composes a subject and body and always sends to your own registered address. It
  cannot email anyone else. A copy is filed onto the transcript as a Markdown attachment.
- **Add as attachment.** It saves prepared content onto a transcript as a Markdown attachment.

Chat and the tools search across every room you belong to, so a meeting shared into a room you are in
turns up in your results.
