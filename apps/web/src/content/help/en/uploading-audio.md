---
title: Uploading audio files
summary: Upload recordings you already have. Diariz accepts WAV, MP3, FLAC, Ogg/Opus, WebM, and M4A, and you can drag several files onto the meetings list at once.
group: getting-started
order: 30
---

Use the **Upload** button above the meetings list, or drag one or more audio files straight onto the
list. Each file gets its own progress and status, so a large batch is easy to watch.

## Supported formats

WAV, MP3, FLAC, Ogg/Opus, WebM, and M4A/AAC.

Diariz identifies the format by inspecting the file's actual contents, not by trusting its extension,
so a mislabelled file is rejected rather than silently failing later in the pipeline. M4A/AAC support
is a server setting an administrator can turn off, because that codec is patented.

There is a maximum upload size (500 MB by default) and a maximum audio duration, both set by the
server.

## What happens next

An uploaded file goes through exactly the same pipeline as a recording made in the app: transcription,
speaker separation, and then whichever AI steps are configured. Diariz measures the true duration of
the audio itself, so the meeting shows the correct length even though the upload carried no timing
information.

## If an upload fails

The most common causes are a format the server does not accept, a file over the size limit, or audio
longer than the configured maximum. The per-file status tells you which. Re-encoding to WAV or MP3
resolves most format problems.
