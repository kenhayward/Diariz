/// Pure model for the desktop Outlook connector: the read window, normalising what the reader returns into
/// what the API expects, the tray items, and the user-facing copy for each failure.
///
/// Everything here is pure and unit-tested (`node --test`) - main.js keeps the Electron and state handling,
/// exactly as recorderState.js / updateState.js / screenshotState.js do for their features.

/// Matches the server's defaults, so a device that has never synced reads the same window the server would
/// have configured for it.
const SYNC_DEFAULTS = { pastDays: 30, futureDays: 180, skipPrivate: true, includeBody: true };

/// Server ceilings, applied here so an over-large read is trimmed before it goes near the network.
const MAX_EVENTS = 5000;
const MAX_BODY_CHARS = 4000;

/// Minimum gap between runs on this machine. The server enforces its own 60s cooldown and answers 409; this
/// just avoids making the request at all.
const SYNC_COOLDOWN_MS = 60_000;

/// Minimum gap between *quick* (today-only) runs. Far shorter than the full one on purpose: a quick sync is
/// what the user reaches for the moment a meeting they can see in Outlook is missing here, which is usually
/// seconds after a full sync has just run. Reading one day is cheap enough that rate-limiting it hard buys
/// nothing. The server applies the same distinction to a narrow window.
const QUICK_SYNC_COOLDOWN_MS = 10_000;

/// The rolling window to read, as ISO instants. Takes `now` rather than calling Date.now() so it is testable.
function windowFor(now, options = {}) {
  const { pastDays, futureDays } = { ...SYNC_DEFAULTS, ...options };
  const start = new Date(now.getTime() - pastDays * 86_400_000);
  const end = new Date(now.getTime() + futureDays * 86_400_000);
  return { start: start.toISOString(), end: end.toISOString() };
}

/// Today, as ISO instants: local midnight to the next local midnight.
///
/// Built by stepping the local date rather than adding 86.4 million milliseconds, because the next local
/// midnight is 23 or 25 hours away across a DST change - and a window that stops an hour short of it would
/// leave the last meetings of the day outside the run (and, worse, outside the server's sweep).
function todayWindow(now) {
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const end = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
  return { start: start.toISOString(), end: end.toISOString() };
}

/// The local-midnight-to-next-local-midnight window for a `yyyy-MM-dd` calendar key, or null when the key is
/// missing or not a real date.
///
/// Built from the parts, never from `new Date(key)`: that parses a bare `yyyy-MM-dd` as UTC midnight, which
/// is the *previous* local day anywhere west of Greenwich - so an American user pressing the button on the
/// 20th would have re-read the 19th. Same class of bug as the all-day-entry one localDateKey exists to avoid.
///
/// The round-trip check is what rejects "2026-13-45": the Date constructor rolls overflowing parts over
/// silently, turning month 13 into January of the next year rather than failing.
function dayWindow(dateKey) {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(dateKey || ""));
  if (!m) return null;
  const year = Number(m[1]);
  const month = Number(m[2]) - 1;
  const day = Number(m[3]);
  const start = new Date(year, month, day);
  if (start.getFullYear() !== year || start.getMonth() !== month || start.getDate() !== day) return null;
  // Day + 1 rather than +24h, so a day containing a DST change stays one whole local day.
  return { start: start.toISOString(), end: new Date(year, month, day + 1).toISOString() };
}

/// The window a run of the given scope should read: one day for the quick sync, the configured rolling window
/// for everything else.
///
/// The quick sync reads `date` (the day the user has selected in the calendar) and falls back to today when it
/// is absent or unusable. The fallback is not just defensive: web and desktop ship separately, so an older web
/// build sends no date at all, and syncing today is exactly what it used to ask for.
function windowForScope(now, options = {}, scope = "all", date = undefined) {
  if (scope !== "today") return windowFor(now, options);
  return dayWindow(date) || todayWindow(now);
}

/// The local calendar date of a `{ year, month, day }` parts object, as `yyyy-MM-dd`.
///
/// Takes parts rather than a Date deliberately: an all-day entry's date must come from LOCAL date parts and
/// never from the UTC instant. An all-day 2026-03-15 in Europe/London is the instant 2026-03-14T23:00:00Z, so
/// anything that goes via toISOString() puts every summer all-day entry a day early - the exact bug in the
/// implementation this was ported from. Taking parts also keeps the test independent of the runner's TZ.
function localDateKey(parts) {
  const pad = (n) => String(n).padStart(2, "0");
  return `${parts.year}-${pad(parts.month)}-${pad(parts.day)}`;
}

/// Local date parts of a Date, for feeding localDateKey. The only impure-ish bit (it reads the machine's zone
/// through the Date), kept separate so the formatting above stays trivially testable.
function readDateParts(date) {
  return {
    year: date.getFullYear(),
    month: date.getMonth() + 1,
    day: date.getDate(),
  };
}

/// Trim a body to the server's cap. The reader already truncates, so this is belt-and-braces for a future
/// reader that does not.
function truncateBody(text, max = MAX_BODY_CHARS) {
  if (typeof text !== "string") return null;
  return text.length <= max ? text : text.slice(0, max);
}

/// Normalise one appointment from the reader into the API's event shape.
///
/// Returns null for anything that must not be sent: an appointment with no usable id or times, and a private
/// one when the user has asked to skip those. A private appointment's body is dropped whatever `includeBody`
/// says - the reader already does this, and so does the server; three layers because the cost of being wrong
/// is someone's private meeting notes on a server they did not expect them on.
function normalizeAppointment(raw, options = {}) {
  const { skipPrivate, includeBody } = { ...SYNC_DEFAULTS, ...options };
  if (!raw || typeof raw.uid !== "string" || raw.uid.length === 0) return null;
  if (!raw.start || !raw.end) return null;

  const sensitivity = Number.isFinite(raw.sensitivity) ? raw.sensitivity : 0;
  const isPrivate = sensitivity >= 2; // olPrivate / olConfidential
  if (skipPrivate && isPrivate) return null;

  const wantBody = includeBody && !isPrivate;

  return {
    // A recurring occurrence is keyed by its uid AND its start.
    //
    // Outlook was expected to stamp the occurrence date into GlobalAppointmentID, giving each instance its
    // own id. Against a real calendar it does not: 87 occurrences came back sharing 24 ids - one per series.
    // Unqualified, the server (which keys a row on source + uid) would collapse every occurrence of a weekly
    // meeting into a single row, and they would overwrite each other on every sync.
    //
    // Qualifying here rather than leaving it to dedupeUids matters for stability: dedupe keeps the FIRST
    // sighting bare and qualifies the rest, so the id of any given occurrence would depend on where the
    // window happened to start - and would change as the window rolled, orphaning its recording link and
    // pre-meeting notes. Deriving it from the occurrence's own start is order-independent and stable.
    uid: raw.isRecurring === true ? `${raw.uid}#${raw.start}` : raw.uid,
    subject: raw.subject ?? null,
    start: raw.start,
    end: raw.end,
    allDay: raw.allDay === true,
    startDate: raw.allDay === true ? (raw.startDate ?? null) : null,
    endDate: raw.allDay === true ? (raw.endDate ?? null) : null,
    windowsTimeZoneId: raw.windowsTimeZoneId ?? null,
    location: raw.location ?? null,
    onlineMeetingUrl: raw.onlineMeetingUrl ?? null,
    bodyText: wantBody ? truncateBody(raw.bodyText) : null,
    categories: raw.categories ?? null,
    sensitivity,
    busyStatus: Number.isFinite(raw.busyStatus) ? raw.busyStatus : 2,
    isRecurring: raw.isRecurring === true,
    organizerName: raw.organizerName ?? null,
    organizerEmail: raw.organizerEmail ?? null,
    attendees: Array.isArray(raw.attendees) ? raw.attendees : [],
    lastModified: raw.lastModified ?? null,
  };
}

/// Make every uid distinct within one batch.
///
/// The server keys a row on (source, uid), so two events sharing one would collapse into a single row and then
/// fight each other on every sync. Outlook's GlobalAppointmentID does encode the occurrence date, so this is
/// belt-and-braces - but a handful of locally-created recurring appointments have been reported returning the
/// same id for several occurrences, and the failure mode is silent data loss.
function dedupeUids(events) {
  const seen = new Set();
  return events.map((e) => {
    if (!seen.has(e.uid)) {
      seen.add(e.uid);
      return e;
    }
    const uid = `${e.uid}#${e.start}`;
    seen.add(uid);
    return { ...e, uid };
  });
}

/// Bound a run to the server's per-run ceiling, keeping the earliest events.
function capEvents(events, max = MAX_EVENTS) {
  return events.length <= max ? events : events.slice(0, max);
}

/// Whether to start a sync now, given `{ inFlight, lastSyncAt, lastQuickSyncAt }`. Mirrors screenshotState's
/// shouldStartCapture.
///
/// The two scopes keep separate stamps deliberately. Sharing one would mean a launch sync locks the quick sync
/// out for a minute - which is exactly the minute the user is staring at a day that is missing a meeting.
function shouldStartSync(state, now, scope = "all") {
  if (!state || state.inFlight) return false;
  return scope === "today"
    ? now - (state.lastQuickSyncAt || 0) >= QUICK_SYNC_COOLDOWN_MS
    : now - (state.lastSyncAt || 0) >= SYNC_COOLDOWN_MS;
}

/// Reader reasons that mean there is no classic Outlook on this PC to talk to - as opposed to one that could
/// not answer just now.
///
/// This distinction is the whole fix for the install prompt: creating the COM object on a PC without classic
/// Outlook makes Windows offer to install Office, and it did so on every launch. A sticky reason is remembered
/// so nothing tries again until the user asks it to from Preferences; a transient one must never be, or a
/// single modal dialog in Outlook would switch the connector off until they went looking for the button.
const STICKY_UNAVAILABLE = ["not-installed", "new-outlook", "unavailable"];

function isStickyUnavailable(reason) {
  return STICKY_UNAVAILABLE.includes(reason);
}

/// The tray items for the connector. Empty unless this machine could actually sync AND the user has opted in -
/// an item that always answered "not available" would be worse than no item. Mirrors trayScreenshotItems.
function trayOutlookItems(state) {
  if (!state || !state.available || !state.enabled) return [];
  const busy = state.phase !== "idle";
  return [
    {
      id: "outlook-sync",
      label: busy ? "Syncing Outlook Calendar…" : "Sync Outlook Calendar",
      enabled: !busy,
    },
  ];
}

/// User-facing copy for a reader failure, by the reason the reader reported.
///
/// The new-Outlook case gets its own message rather than falling into the generic one: it is the case a growing
/// share of Windows users will hit as Microsoft migrates them, and "something went wrong" would leave them with
/// nothing to act on.
function describeComError(reason) {
  switch (reason) {
    case "not-windows":
      return null; // the affordance is hidden entirely; there is nothing to say
    case "unavailable":
      return "Outlook sync isn't available in this build.";
    case "not-installed":
      return "Outlook isn't installed on this PC.";
    case "new-outlook":
      return "Diariz needs classic Outlook for Windows. The new Outlook doesn't let other apps read your calendar.";
    case "busy":
      return "Outlook is busy - try again in a moment.";
    case "denied":
      return "Outlook blocked access to your calendar.";
    case "timeout":
      return "Reading your Outlook calendar took too long.";
    default:
      return "Couldn't read your Outlook calendar.";
  }
}

/// Native-notification copy for a finished sync. Null when there is nothing worth interrupting for - a routine
/// successful sync that changed nothing should not raise a toast every launch.
function notificationForSyncResult(result) {
  if (!result) return null;

  if (!result.ok) {
    return { title: "Outlook sync failed", body: describeComError(result.reason) ?? "Couldn't read your Outlook calendar." };
  }

  const changed = (result.created || 0) + (result.updated || 0) + (result.deleted || 0);
  if (changed === 0) return null;

  const parts = [];
  if (result.created) parts.push(`${result.created} added`);
  if (result.updated) parts.push(`${result.updated} updated`);
  if (result.deleted) parts.push(`${result.deleted} removed`);
  return { title: "Outlook calendar synced", body: parts.join(", ") };
}

module.exports = {
  SYNC_DEFAULTS,
  MAX_EVENTS,
  MAX_BODY_CHARS,
  SYNC_COOLDOWN_MS,
  QUICK_SYNC_COOLDOWN_MS,
  STICKY_UNAVAILABLE,
  windowFor,
  todayWindow,
  windowForScope,
  dayWindow,
  isStickyUnavailable,
  localDateKey,
  readDateParts,
  truncateBody,
  normalizeAppointment,
  dedupeUids,
  capEvents,
  shouldStartSync,
  trayOutlookItems,
  describeComError,
  notificationForSyncResult,
};
