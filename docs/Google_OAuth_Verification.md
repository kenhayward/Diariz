# Google OAuth verification — Diariz

Reference for taking the Diariz Google OAuth app through Google's verification, and the ready-to-publish
copy it requires. Internal doc (no version number).

## Scope inventory (what Diariz requests)

| Scope | Purpose | Google tier | Verification burden |
|---|---|---|---|
| `openid`, `email`, `profile` | Sign in with Google; read name, email, profile picture | Non-sensitive | None (no review) |
| `https://www.googleapis.com/auth/calendar.readonly` | Read the user's primary calendar to show the meeting a recording overlaps | **Sensitive** | Standard app verification (no security assessment) |

**We deliberately do NOT request any Gmail scope.** Gmail scopes (e.g. `gmail.compose`) are Google
**restricted** scopes, which require a recurring third-party **security assessment (CASA)** on top of
verification. The Gmail-draft feature was removed in 0.67.1; "email the minutes to yourself" (sent from the
platform's own mailbox, not the user's Google account) covers the same need without any Gmail scope. Keeping
Diariz to `calendar.readonly` means **standard verification only — no security assessment**.

> If a future feature needs a restricted scope, verification jumps a tier (annual CASA, ~thousands USD/yr).
> Weigh that before adding one.

## Verification checklist (owner)

Do these once the app is ready to move out of "Testing":

1. **Verify domain ownership** — add and verify every domain used (app homepage, privacy policy, OAuth
   redirect) in **Google Search Console**, under the same Google account that owns the Cloud project.
2. **OAuth consent screen** — fill in app name, **app logo** (own review), user-support email, developer
   contact email, and **authorized domains**. Set publishing status to **In production**.
3. **App homepage** — a public page on a domain you own (`diariz.stocks-hayward.com`) that describes what
   Diariz does and links to the privacy policy. Not a bare redirect or a login wall.
4. **Privacy policy** — hosted on your domain, linked from **both** the homepage and the consent screen. Must
   describe how Diariz accesses/uses/stores/shares Google user data (draft below).
5. **Scope justification** — per-scope explanation of why it's needed and how the data is used (draft below).
6. **Demo video** — an unlisted/public YouTube video showing the **OAuth consent flow with the client ID
   visible in the browser URL**, and the `calendar.readonly` scope actually being used (connect Calendar in
   Preferences → open a recording → the Overview shows the matching meeting).
7. **Submit for verification.** Sensitive-scope review typically takes a few business days.

Until submitted/approved, keep the app in **Testing** with the owner + up to 100 **test users** added — the
sensitive Calendar scope works for those accounts with only the "unverified app" warning.

---

## Draft 1 — Privacy-policy section (Google user data)

Paste into the published privacy policy (adjust the operator name / contact if desired).

> ### Google user data
>
> When you choose to sign in with Google or connect Google Calendar, Diariz accesses a limited set of data
> from your Google Account, only with your explicit consent and only for the purposes described here.
>
> **What we access**
> - **Basic profile** (name, email address, profile picture), via the `openid`, `email`, and `profile`
>   scopes — used to create and identify your Diariz account and to show your name and picture in the app.
> - **Google Calendar (read-only)**, via the `calendar.readonly` scope — used solely to find the calendar
>   event that overlaps a recording's time, so Diariz can show you the meeting a recording belongs to. We read
>   event times, titles, and links; we never create, modify, or delete calendar data.
>
> **How we use it** — Google user data is used only to provide the features above, at your request. We do not
> use it for advertising, we do not sell it, and we do not use it to train generalized AI/ML models.
>
> **How we store it** — Your name, email, and picture are stored with your account. To read your calendar,
> Diariz stores a Google **refresh token encrypted at rest**; short-lived access tokens are held only in
> memory and are never written to disk or exposed to your browser. Calendar events themselves are **not**
> stored — they are fetched on demand and discarded.
>
> **How to revoke** — You can disconnect Google Calendar at any time from **Preferences → Google** in Diariz
> (which revokes the token at Google), or from your Google Account's **Security → Third-party access** page.
> Revoking immediately stops all further access.
>
> **Limited Use** — Diariz's use and transfer of information received from Google APIs adheres to the
> [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
> including its **Limited Use** requirements.

---

## Draft 2 — Per-scope justification (for the verification form)

Google asks, per requested scope, "Why does your app need access to this scope?" Use these.

> **`.../auth/calendar.readonly`**
> Diariz is a meeting-transcription tool. After a meeting is recorded and transcribed, we help the user
> confirm which meeting a recording belongs to by showing the calendar event it overlaps in time. To do this
> we read the user's primary calendar events (start/end time, title, and link) for the time window around the
> recording, and display the single best-matching event on the recording's overview page. This is **read-only**
> — Diariz never creates, edits, or deletes calendar data — and it runs only after the user explicitly opts in
> from Preferences. We request the narrowest scope that supports this (`calendar.readonly`) rather than full
> calendar access. Access tokens are short-lived and kept in memory; the refresh token is stored encrypted and
> is revoked when the user disconnects.

> **`openid`, `email`, `profile`** (if asked)
> Used for "Sign in with Google": to create/identify the user's Diariz account and display their name and
> profile picture in the app. No other use.

---

## Notes for reviewers / future maintainers

- The consent, token storage, and calendar read live in `AuthController` (`google/connect`, `google/callback`,
  `google/disconnect`), `GoogleTokenProvider`/`GoogleTokenProtector`, and `GoogleCalendarClient`. See
  `docs/Overall_Synopsis_of_Platform.md` → Google data access.
- **Keep this app sensitive-only.** Adding any Gmail/Drive/Contacts *restricted* scope re-triggers the CASA
  security assessment and its annual cost. If a feature seems to need one, first check whether a
  server-side/self-mailbox alternative avoids it (as the minutes-email feature does).
