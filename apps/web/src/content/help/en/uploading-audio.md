---
title: Uploading audio and video files
summary: Upload recordings you already have. Diariz accepts WAV, MP3, FLAC, Ogg/Opus, WebM, and M4A, and you can drop a video (MP4, MOV, MKV) to transcribe its audio. Video is converted in your browser, so only the audio is ever uploaded.
group: getting-started
order: 30
---

Use the **Upload** button above the meetings list, or drag one or more files straight onto the
list. Each file gets its own progress and status, so a large batch is easy to watch.

## Supported formats

Audio: WAV, MP3, FLAC, Ogg/Opus, WebM, and M4A/AAC.

Video: MP4, MOV, MKV, and WebM.

Diariz identifies the format by inspecting the file's actual contents, not by trusting its extension,
so a mislabelled file is rejected rather than silently failing later in the pipeline. M4A/AAC support
is a server setting an administrator can turn off, because that codec is patented.

There is a maximum upload size (500 MB by default) and a maximum audio duration, both set by the
server. For a video, the limit applies to the extracted audio, not to the video file - so a 3 GB
recording is fine, because what gets uploaded is usually well under 100 MB.

## Dropping a video

Only the audio is ever uploaded. When you drop a video, Diariz extracts its sound track in your
browser, mixes it down to mono, and sends just that. The video itself never leaves your machine and
is never stored, so it costs you nothing against your storage quota.

A long recording takes a little while to process, so the file's row shows "Extracting audio" with a
percentage, and a **Cancel** link if you change your mind. Nothing is uploaded until extraction has
finished.

Audio files are not affected: they upload exactly as they always have. Re-uploading a recording that
Diariz itself made is passed straight through without being processed again.

This works in Chrome, Edge, and the desktop app. A browser without the necessary media support says
so rather than uploading the video.

## What happens next

An uploaded file goes through exactly the same pipeline as a recording made in the app: transcription,
speaker separation, and then whichever AI steps are configured. Diariz measures the true duration of
the audio itself, so the meeting shows the correct length even though the upload carried no timing
information.

## If an upload fails

The most common causes are a format the server does not accept, a file over the size limit, or audio
longer than the configured maximum. The per-file status tells you which. Re-encoding to WAV or MP3
resolves most format problems.

A video can also fail because it has no sound track at all, or uses an audio codec your browser
cannot decode. Diariz never falls back to uploading the video itself, so you will get a clear message
instead of a surprise upload.
