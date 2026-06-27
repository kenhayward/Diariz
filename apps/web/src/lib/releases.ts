// Release notes + About-box copy. This is the single source for the Release Notes page and the
// About modal. Per the CLAUDE.md versioning rule, every PR adds/updates the newest RELEASES entry
// and RELEASES[0].version must equal the app version (version.json).

export const TAGLINE = "Smart Meeting Transcription";
export const GITHUB_URL = "https://github.com/kenhayward/Diariz";
export const COPYRIGHT = "© Ken Hayward";
export const LICENSE = "Apache-2.0";

/// Short summary of what the app does today, shown in the About box (markdown). Update this whenever
/// the app's scope changes.
export const CAPABILITIES = `
Diariz records audio — from your microphone, or Windows system/loopback audio via the desktop app —
and transcribes it server-side with **WhisperX** (word-level timestamps) and **pyannote** speaker
diarization. You get speaker-labelled, timestamped segments you can rename, edit, and play back
(per segment or the whole recording), and can re-transcribe at any time.

It can **summarise** recordings and let you **chat across one or more transcripts** — with file
attachments, a context-usage dial, and saved conversations — using an OpenAI-compatible LLM endpoint
you configure. Recordings organise into **sections** with drag-and-drop ordering.

Diariz is **multi-user** with role-based access: people request access (or an administrator adds them),
an administrator approves, and each user sets up their own account and keeps their own private
recordings, transcripts, and chats.
`.trim();

export interface Release {
  version: string;
  date: string; // ISO yyyy-mm-dd
  pr?: number;
  headline: string;
  summary: string; // markdown; PR-level detail
  added?: string[];
  changed?: string[];
  fixed?: string[];
}

/// Newest first. RELEASES[0].version must match version.json (asserted in releases.test.ts).
export const RELEASES: Release[] = [
  {
    version: "0.2.1",
    date: "2026-06-27",
    pr: 23,
    headline: "Research note: speaker identification & verification options",
    summary: `
A documentation-only release. Adds a research note,
\`docs/Speaker_Identification_and_Verification.md\`, surveying open-source options for **speaker
identification and verification by embedding comparison** — recognising an enrolled person ("is this Alice?")
across recordings, beyond anonymous diarization.

It covers how embedding-based recognition works (enrol → cosine-match → threshold, with open-set "unknown"),
compares the main toolkits (SpeechBrain ECAPA-TDNN, WeSpeaker, NVIDIA NeMo TitaNet, 3D-Speaker, WavLM,
Resemblyzer), untangles the code / weights / training-data licensing layers (the VoxCeleb non-commercial
caveat), and sketches how identification could reuse the embedding pyannote already computes plus the existing
pgvector storage. Recommendation: **SpeechBrain ECAPA-TDNN** for a true-open-source, lightweight start. No app
behaviour changes.
`.trim(),
    added: ["Research note on open-source speaker-embedding identification/verification (`docs/`)."],
  },
  {
    version: "0.2.0",
    date: "2026-06-27",
    pr: 22,
    headline: "Admins can add users directly from Manage Users",
    summary: `
Administrators can now **create a user by email** from the **Manage Users** modal, without waiting for
that person to request access. Adding a user creates the account and runs the same onboarding as an
approved request: a **one-time setup link** is emailed to them (or shown to the admin to share when SMTP
isn't configured), and they finish by setting their full name and password.

Each user's **onboarding status** is now surfaced as a pill in the modal — *Requested* (awaiting an
admin's grant), *Awaiting setup* (invited, link sent, not yet completed), or *Active* — so it's clear at
a glance where everyone is in the process.
`.trim(),
    added: [
      "“Add user” by email in the Manage Users modal — creates the account and sends the setup link (with the no-SMTP fallback link shown to the admin).",
      "Onboarding status pill per user (Requested / Awaiting setup / Active).",
    ],
  },
  {
    version: "0.1.0",
    date: "2026-06-27",
    pr: 21,
    headline: "First tagged release — versioning, release notes, and an About box",
    summary: `
The first versioned release of Diariz. It introduces a **Major.Minor.Build** scheme, this **Release
Notes** page, and an **About** box in the account menu (with the app version, build, a capabilities
summary, links to these notes and GitHub, third-party disclaimers, and copyright). The API now reports
its version at \`/health\`.

This entry also serves as the baseline summary of everything Diariz does today:

- **Capture** audio from the browser microphone, or Windows system/loopback audio via the Electron
  desktop shell.
- **Transcribe** server-side with WhisperX (faster-whisper large-v3) for word-level timestamps and
  **pyannote 3.1** for speaker diarization, orchestrated through a Redis-stream job queue with live
  status over SignalR.
- **Review** speaker-labelled, timestamped segments: rename speakers (preserved across re-transcribes),
  edit segment text, and play back per-segment or the whole recording. Re-transcribe with a chosen model.
- **Summarise** recordings (with automatic naming) and **chat across one or more transcripts** —
  streaming replies, a context-usage dial, PDF/text attachments, and saved/reloadable conversations —
  via a per-user (or server-default) OpenAI-compatible LLM endpoint, with the API key encrypted at rest.
- **Organise** recordings into sections with drag-and-drop ordering and cross-group moves.
- **Multi-user RBAC**: Standard / Administrator / Platform Administrator roles, an access-request →
  admin-grant → account-setup lifecycle (one-time email link, with an in-app fallback when SMTP is
  unconfigured), and admin user management. Every user's data is isolated to them.
- Light/Dark/Auto theming throughout.
`.trim(),
    added: [
      "Major.Minor.Build versioning with a single canonical source (version.json), surfaced in the About box and at GET /health.",
      "Release Notes page (fixed header, release list, per-release detail).",
      "About box in the account menu: version + build, capabilities, Release Notes & GitHub links, third-party disclaimers, copyright.",
    ],
  },
];
