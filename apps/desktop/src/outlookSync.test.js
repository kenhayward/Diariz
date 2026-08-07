const test = require("node:test");
const assert = require("node:assert/strict");

const {
  SYNC_DEFAULTS, MAX_EVENTS, SYNC_COOLDOWN_MS,
  windowFor, localDateKey, readDateParts, truncateBody, normalizeAppointment,
  dedupeUids, capEvents, shouldStartSync, trayOutlookItems, describeComError, notificationForSyncResult,
} = require("./outlookSync");

// ---- windowFor ----

test("windowFor spans the configured days either side of now", () => {
  const now = new Date("2026-07-02T12:00:00Z");
  const { start, end } = windowFor(now, { pastDays: 30, futureDays: 180 });
  assert.equal(start, "2026-06-02T12:00:00.000Z");
  assert.equal(end, "2026-12-29T12:00:00.000Z");
});

test("windowFor falls back to the server's own defaults", () => {
  const now = new Date("2026-07-02T12:00:00Z");
  const a = windowFor(now);
  const b = windowFor(now, SYNC_DEFAULTS);
  assert.deepEqual(a, b);
});

// ---- the all-day off-by-one ----

test("localDateKey formats local date parts, never a UTC instant", () => {
  assert.equal(localDateKey({ year: 2026, month: 3, day: 15 }), "2026-03-15");
  assert.equal(localDateKey({ year: 2026, month: 12, day: 1 }), "2026-12-01");
});

// The regression. An all-day 2026-03-15 in a UTC+1 zone is the instant 2026-03-14T23:00:00Z, so anything that
// derives the date from the instant reports the previous day - for every summer all-day entry. Written against
// a parts object so it does not depend on the test runner's own timezone.
test("localDateKey keeps the local date even when the UTC instant is the previous day", () => {
  const localMidnight = new Date(2026, 2, 15, 0, 0, 0); // 15 March, local
  const parts = readDateParts(localMidnight);
  assert.equal(localDateKey(parts), "2026-03-15");
  // Fails if it had gone via toISOString() in a zone east of UTC.
  assert.notEqual(localDateKey(parts), "2026-03-14");
});

// ---- normalizeAppointment ----

const raw = {
  uid: "0400008200E00074C5B7101A82E008",
  subject: "Planning",
  start: "2026-07-02T09:00:00Z",
  end: "2026-07-02T10:00:00Z",
  sensitivity: 0,
  busyStatus: 2,
  bodyText: "Agenda",
  attendees: [{ name: "Bob", email: "bob@x.test", response: "accepted", optional: false }],
  lastModified: "2026-07-01T08:00:00Z",
};

// Outlook was expected to give each occurrence of a series its own GlobalAppointmentID. Against a real
// calendar it does not - 87 occurrences came back sharing 24 ids. The server keys a row on source + uid, so
// unqualified these would collapse into one row per series and overwrite each other on every sync.
test("normalizeAppointment keys a recurring occurrence by its start as well as its uid", () => {
  const series = { ...raw, isRecurring: true };
  const first = normalizeAppointment({ ...series, start: "2026-07-02T09:00:00Z" });
  const second = normalizeAppointment({ ...series, start: "2026-07-09T09:00:00Z" });

  assert.notEqual(first.uid, second.uid);
  assert.equal(first.uid, `${raw.uid}#2026-07-02T09:00:00Z`);
});

// Order-independence is the point. Qualifying only on a second sighting (which is what dedupeUids does)
// would make an occurrence's id depend on where the rolling window happened to start, so it would change as
// the window moved - orphaning its recording link and pre-meeting notes.
test("a recurring occurrence keeps the same uid whatever order it arrives in", () => {
  const series = { ...raw, isRecurring: true };
  const alone = normalizeAppointment({ ...series, start: "2026-07-09T09:00:00Z" });
  const afterAnother = [
    { ...series, start: "2026-07-02T09:00:00Z" },
    { ...series, start: "2026-07-09T09:00:00Z" },
  ].map((e) => normalizeAppointment(e))[1];

  assert.equal(alone.uid, afterAnother.uid);
});

test("a one-off appointment keeps its uid unqualified", () => {
  assert.equal(normalizeAppointment({ ...raw, isRecurring: false }).uid, raw.uid);
});

test("normalizeAppointment maps a timed appointment", () => {
  const e = normalizeAppointment(raw);
  assert.equal(e.uid, raw.uid);
  assert.equal(e.subject, "Planning");
  assert.equal(e.allDay, false);
  assert.equal(e.startDate, null); // only all-day entries carry dates
  assert.equal(e.bodyText, "Agenda");
  assert.equal(e.attendees.length, 1);
});

test("normalizeAppointment keeps all-day dates and drops them for timed events", () => {
  const allDay = normalizeAppointment({ ...raw, allDay: true, startDate: "2026-07-02", endDate: "2026-07-03" });
  assert.equal(allDay.allDay, true);
  assert.equal(allDay.startDate, "2026-07-02");
  assert.equal(allDay.endDate, "2026-07-03");

  // A timed event that somehow carried dates must not keep them - they would be read as an all-day span.
  const timed = normalizeAppointment({ ...raw, allDay: false, startDate: "2026-07-02" });
  assert.equal(timed.startDate, null);
});

test("normalizeAppointment rejects anything unusable", () => {
  assert.equal(normalizeAppointment(null), null);
  assert.equal(normalizeAppointment({ ...raw, uid: "" }), null);
  assert.equal(normalizeAppointment({ ...raw, uid: undefined }), null);
  assert.equal(normalizeAppointment({ ...raw, start: null }), null);
  assert.equal(normalizeAppointment({ ...raw, end: undefined }), null);
});

test("normalizeAppointment skips private appointments by default", () => {
  assert.equal(normalizeAppointment({ ...raw, sensitivity: 2 }), null); // olPrivate
  assert.equal(normalizeAppointment({ ...raw, sensitivity: 3 }), null); // olConfidential
  // olPersonal is not private - it syncs, matching the reference implementation's own filter.
  assert.ok(normalizeAppointment({ ...raw, sensitivity: 1 }));
});

test("normalizeAppointment strips a private appointment's body even when bodies are wanted", () => {
  const e = normalizeAppointment({ ...raw, sensitivity: 2 }, { skipPrivate: false, includeBody: true });
  assert.ok(e, "a private item should still sync when skipPrivate is off");
  assert.equal(e.bodyText, null, "but never with its body");
});

test("normalizeAppointment drops every body when bodies are excluded", () => {
  assert.equal(normalizeAppointment(raw, { includeBody: false }).bodyText, null);
});

test("truncateBody cuts at the cap and tolerates a missing body", () => {
  assert.equal(truncateBody("x".repeat(5000)).length, 4000);
  assert.equal(truncateBody("short"), "short");
  assert.equal(truncateBody(null), null);
  assert.equal(truncateBody(undefined), null);
});

// ---- dedupe / cap ----

test("dedupeUids disambiguates two occurrences reported with the same id", () => {
  const events = [
    { uid: "same", start: "2026-07-02T09:00:00Z" },
    { uid: "same", start: "2026-07-09T09:00:00Z" },
    { uid: "other", start: "2026-07-03T09:00:00Z" },
  ];
  const out = dedupeUids(events);
  assert.equal(new Set(out.map((e) => e.uid)).size, 3);
  assert.equal(out[0].uid, "same");                          // first sighting is untouched
  assert.equal(out[1].uid, "same#2026-07-09T09:00:00Z");     // the clash is qualified by its start
});

test("dedupeUids leaves an already-distinct batch alone", () => {
  const events = [{ uid: "a", start: "x" }, { uid: "b", start: "y" }];
  assert.deepEqual(dedupeUids(events), events);
});

test("capEvents bounds a run to the server's ceiling", () => {
  const many = Array.from({ length: MAX_EVENTS + 25 }, (_, i) => ({ uid: `u${i}` }));
  assert.equal(capEvents(many).length, MAX_EVENTS);
  assert.equal(capEvents([{ uid: "a" }]).length, 1);
});

// ---- cooldown ----

test("shouldStartSync refuses while one is running or inside the cooldown", () => {
  const now = 1_000_000;
  assert.equal(shouldStartSync({ inFlight: true, lastSyncAt: 0 }, now), false);
  assert.equal(shouldStartSync({ inFlight: false, lastSyncAt: now - 1000 }, now), false);
  assert.equal(shouldStartSync({ inFlight: false, lastSyncAt: now - SYNC_COOLDOWN_MS }, now), true);
  assert.equal(shouldStartSync({ inFlight: false, lastSyncAt: 0 }, now), true);
  assert.equal(shouldStartSync(null, now), false);
});

// ---- tray ----

test("trayOutlookItems shows nothing unless Outlook is reachable and the user opted in", () => {
  assert.deepEqual(trayOutlookItems({ available: false, enabled: true, phase: "idle" }), []);
  assert.deepEqual(trayOutlookItems({ available: true, enabled: false, phase: "idle" }), []);
  assert.deepEqual(trayOutlookItems(null), []);
});

test("trayOutlookItems offers a sync, disabled while one runs", () => {
  const idle = trayOutlookItems({ available: true, enabled: true, phase: "idle" });
  assert.equal(idle.length, 1);
  assert.equal(idle[0].enabled, true);
  assert.match(idle[0].label, /Sync Outlook Calendar/);

  const busy = trayOutlookItems({ available: true, enabled: true, phase: "reading" });
  assert.equal(busy[0].enabled, false);
  assert.match(busy[0].label, /Syncing/);
});

// ---- failure copy ----

test("describeComError names the new Outlook specifically", () => {
  const msg = describeComError("new-outlook");
  assert.match(msg, /classic Outlook/);
  assert.notEqual(msg, describeComError("error"), "it must not collapse into the generic message");
});

test("describeComError covers every reason the reader can report", () => {
  for (const reason of ["unavailable", "not-installed", "new-outlook", "busy", "denied", "timeout", "error"]) {
    const msg = describeComError(reason);
    assert.equal(typeof msg, "string", `${reason} needs copy`);
    assert.ok(msg.length > 0);
  }
  // Not being on Windows hides the feature entirely, so there is nothing to say.
  assert.equal(describeComError("not-windows"), null);
});

// ---- notifications ----

test("notificationForSyncResult stays quiet when a sync changed nothing", () => {
  assert.equal(notificationForSyncResult({ ok: true, created: 0, updated: 0, deleted: 0 }), null);
  assert.equal(notificationForSyncResult(null), null);
});

test("notificationForSyncResult reports what changed", () => {
  const n = notificationForSyncResult({ ok: true, created: 3, updated: 1, deleted: 2 });
  assert.match(n.body, /3 added/);
  assert.match(n.body, /1 updated/);
  assert.match(n.body, /2 removed/);
});

test("notificationForSyncResult explains a failure using the reader's reason", () => {
  const n = notificationForSyncResult({ ok: false, reason: "new-outlook" });
  assert.match(n.title, /failed/i);
  assert.match(n.body, /classic Outlook/);
});
