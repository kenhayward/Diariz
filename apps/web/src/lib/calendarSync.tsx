import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { formatLongDate } from "./format";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./api";
import { useStatus } from "./status";
import {
  canSyncOutlook,
  onOutlookState,
  outlookAvailable,
  syncOutlookNow,
  type OutlookSyncScope,
} from "./outlookSync";

/// One refresh of every calendar the user actually has - the single control behind the Calendar toolbar's two
/// sync buttons.
///
/// There is deliberately no per-provider fan-out. Google and subscribed `.ics` feeds are read **live** by the
/// server on every `/api/calendar/events` request, and it skips whichever of them is not connected - so
/// refetching the overlay *is* their refresh, and it costs nothing for a user who has neither. Desktop Outlook
/// is the one exception: it lives on the user's PC, only the shell can read it, and it has to be harvested and
/// pushed before the server has anything new to return. Hence: ask the shell (when there is one), wait for it,
/// then refetch once for everybody.

export type CalendarSyncScope = OutlookSyncScope;

/// How long to wait for the desktop shell before giving up on it. A first read of a large mailbox with bodies
/// genuinely takes tens of seconds; the reader's own ceiling is 120s, so this sits just past it.
const SHELL_TIMEOUT_MS = 150_000;

export interface CalendarSyncDeps {
  /// Whether the desktop Outlook mirror is connected on this machine and should take part.
  outlook: boolean;
  syncOutlookNow: (options: { scope: CalendarSyncScope; date?: string }) => Promise<{ started: boolean; reason?: string }>;
  onOutlookState: (cb: (state: { phase: string }) => void) => () => void;
  /// Refresh the calendar overlay. This is what picks up Google and the `.ics` feeds.
  refetchEvents: () => Promise<unknown>;
  timeoutMs?: number;
}

export interface CalendarSyncResult {
  /// Why the shell did not run, when it did not (`cooldown`, `busy`, `timeout`, ...). Absent on success and
  /// when there was no shell to ask.
  outlookReason?: string;
}

/// Whole seconds since the sync started, floored at zero so a system clock that moves backwards mid-run cannot
/// put a negative count in the status bar.
export function elapsedSeconds(startedAt: number, now: number): number {
  return Math.max(0, Math.floor((now - startedAt) / 1000));
}

/// The status-bar message for a run in progress (workspace namespace). Naming the scope matters: the two
/// buttons take very different amounts of time, and "still going" reads differently when you know which one
/// you pressed.
export function syncStatusKey(scope: CalendarSyncScope): string {
  return scope === "today" ? "statusSyncingCalendarDay" : "statusSyncingCalendar";
}

/// Local midnight of a `yyyy-MM-dd` calendar key as an ISO instant, or of today when the key is absent -
/// which is exactly when the shell falls back to today, so the message and the sync agree.
///
/// Parsed from the parts rather than handed to `new Date(key)`: that reads a bare `yyyy-MM-dd` as UTC
/// midnight, so the status bar would name the previous day for anyone west of Greenwich while the shell
/// (which parses locally) read the right one.
export function dayStartIso(dateKey?: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(dateKey ?? "");
  if (!m) return new Date().toISOString();
  return new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])).toISOString();
}

/// The message for a run that did not happen (workspace namespace), or null when there is nothing worth
/// saying.
///
/// A refusal is not automatically a failure. `cooldown` means a sync ran moments ago, so the calendar is
/// already as fresh as it can be - shouting at the user for pressing a button twice is worse than silence.
/// `busy` never reaches here at all: a run already in flight is one to join, not to report.
///
/// Everything else names what went wrong. The first version of this said only "Could not sync the calendar",
/// which threw the reason away at exactly the moment it was needed - the first real failure took an hour to
/// diagnose because the screen held no evidence at all.
export function syncErrorKey(reason: string | undefined): string | null {
  if (!reason || reason === "cooldown") return null;
  switch (reason) {
    // Every "there is no Outlook here to talk to" answer reads the same to a user, whichever layer said it.
    case "unavailable":
    case "not-installed":
    case "new-outlook":
    case "not-windows":
      return "calSyncFailedUnavailable";
    // Should not be reachable - the buttons only ask the shell when the opt-in is on - but if the two ever
    // disagree, "turn it on in Preferences" is the one thing that would actually help.
    case "disabled":
      return "calSyncFailedDisabled";
    case "timeout":
      return "calSyncFailedTimeout";
    case "denied":
      return "calSyncFailedDenied";
    default:
      return "calSyncFailed";
  }
}

/// Run one sync. Resolves once every source has been refreshed - which is the moment the caller can stop
/// showing "syncing".
export async function runCalendarSync(
  scope: CalendarSyncScope,
  deps: CalendarSyncDeps,
  /// The day to read, as a `yyyy-MM-dd` local calendar key. Only meaningful for the quick sync; the full
  /// one reads the whole configured window by definition, so it is dropped there rather than sent and
  /// ignored. Absent means "today", which is what the shell falls back to.
  date?: string,
): Promise<CalendarSyncResult> {
  const result: CalendarSyncResult = {};

  if (deps.outlook) {
    const reason = await syncOutlookAndWait(scope, scope === "today" ? date : undefined, deps);
    if (reason) result.outlookReason = reason;
  }

  // Always, even when the shell refused: Google and the feeds are still worth re-reading, and a button that
  // did nothing at all because Outlook was busy would look broken.
  await deps.refetchEvents();
  return result;
}

/// Ask the shell to harvest, and wait until it says it has finished.
///
/// Waiting is the load-bearing part. The shell returns to `idle` only once the renderer has POSTed the window
/// it harvested, so that is the first moment the server has the new meetings; refreshing any earlier redraws
/// the ones already on screen and leaves the new one invisible until something else refetches.
///
/// Resolves to a reason when the run did not happen (or never came back), else undefined.
function syncOutlookAndWait(
  scope: CalendarSyncScope,
  date: string | undefined,
  deps: CalendarSyncDeps,
): Promise<string | undefined> {
  return new Promise((resolve) => {
    let settled = false;
    // Whether a run is known to be under way, and therefore whether an `idle` means "finished" rather than
    // "nothing has started yet". Set by seeing a working phase, and by the shell telling us it is busy.
    let running = false;

    const finish = (reason?: string) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      unsubscribe();
      resolve(reason);
    };

    // Subscribed before the sync is asked for: the shell replays nothing, and a fast read could otherwise be
    // over before this listener existed.
    const unsubscribe = deps.onOutlookState((state) => {
      if (state.phase !== "idle") running = true;
      else if (running) finish();
    });
    const timer = setTimeout(() => finish("timeout"), deps.timeoutMs ?? SHELL_TIMEOUT_MS);

    void deps
      .syncOutlookNow({ scope, date })
      .then(({ started, reason }) => {
        if (started) return;
        // `busy` means a sync is ALREADY RUNNING - the one that fires on launch, or the tray's. It refreshes
        // the same calendar we were about to ask for, so join it instead of treating it as a failure. This is
        // what used to put a red error on screen for the whole of every launch sync.
        //
        // Marking it running is not incidental: we are attaching to a run already under way, so the `idle`
        // that ends it may be the only event we ever see. Waiting for a working phase first would mean waiting
        // out the timeout on exactly the runs we most want to follow.
        //
        // A reader failure also reports `busy` (Outlook itself refused, mid-dialog), but that arrives after
        // the shell has already been through reading -> idle, so `finish` has run and this is a no-op. The
        // shell raises its own notification for those.
        if (reason === "busy") {
          running = true;
          return;
        }
        finish(reason ?? "error");
      })
      .catch(() => finish("error"));
  });
}

/// The Calendar toolbar's sync controls: what is running, how long it has been running, and how to start one.
export interface CalendarSyncValue {
  /// The scope of the run in progress, or null when idle.
  syncing: CalendarSyncScope | null;
  /// Whether **any** sync is under way, including one this app did not start - the shell's launch sync, or one
  /// from the tray. The buttons disable on this, not on `syncing`.
  busy: boolean;
  /// Start a run. Ignored while one is already going; the same two buttons work in a browser, where a sync is
  /// simply Google and the feeds with no shell to ask.
  ///
  /// `date` is the day the quick sync should read, as a `yyyy-MM-dd` local calendar key - the day selected in
  /// the calendar, not necessarily today. Ignored for the full sync.
  sync: (scope: CalendarSyncScope, date?: string) => void;
}

/// The run's whole state machine. Lives in `CalendarSyncProvider`, never in a component that comes and goes -
/// see the provider's own note for why.
function useCalendarSyncState(): CalendarSyncValue {
  const { t, i18n } = useTranslation("workspace");
  const qc = useQueryClient();
  const { setStatus } = useStatus();
  const [syncing, setSyncing] = useState<CalendarSyncScope | null>(null);
  const startedAt = useRef(0);
  const syncingDate = useRef<string | undefined>(undefined);

  const { data: settings } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });
  const [shellReady, setShellReady] = useState(false);
  useEffect(() => {
    let live = true;
    void outlookAvailable().then((ok) => {
      if (live) setShellReady(ok);
    });
    return () => {
      live = false;
    };
  }, []);
  const outlook = canSyncOutlook() && shellReady && settings?.outlookSyncEnabled === true;

  // Follow the shell's own phase, so a sync nobody here started is still visible.
  //
  // This is what the old Sync Outlook link did, and dropping it when the buttons moved up here is what broke
  // them: the shell refuses a second run while one is in flight, and the launch sync holds it for tens of
  // seconds every time the app opens. Buttons that stay live through that are buttons that fail.
  const [shellPhase, setShellPhase] = useState<"idle" | "reading" | "pushing">("idle");
  // `reported` is cleared on every phase change: a shell that is doing something NEW has earned a progress
  // line again, whatever verdict the last run ended on. See the ticking effect below.
  const reported = useRef(false);
  useEffect(
    () =>
      onOutlookState((s) => {
        reported.current = false;
        setShellPhase(s.phase);
      }),
    [],
  );

  // Believing the shell's phase forever is what made this half of `busy` unbounded. The shell's own reader is
  // hard-capped at 120s (`READ_TIMEOUT_MS`), so silence for longer than the ceiling below is not a slow
  // mailbox - it is a shell that is never coming back. It genuinely happens: the shell parks in `pushing`
  // until the renderer reports its POST, and nothing resets it if that report never arrives (a reload mid-push
  // is enough). Left unbounded that disabled both sync buttons and held the status line for the rest of the
  // session - the calendar could not be synced again without restarting the app.
  //
  // The timer restarts on every phase change, so a run that keeps reporting is never cut short however long it
  // takes; only silence expires. Forcing `idle` here is local disbelief, not a claim about the shell - if it
  // wakes up and reports again, the subscription above picks it straight back up.
  useEffect(() => {
    if (shellPhase === "idle") return;
    const stale = setTimeout(() => setShellPhase("idle"), SHELL_TIMEOUT_MS);
    return () => clearTimeout(stale);
  }, [shellPhase]);

  const busy = syncing != null || shellPhase !== "idle";

  // The count-up. A ticking message is the whole point of it: a 30-second sync with a static label is
  // indistinguishable from one that has hung. Shown immediately rather than on the first tick a second later,
  // so the bar reacts to the click.
  //
  // `pushed` mirrors the recorder's guard: only ever clear a message we put there ourselves. The completion
  // handler below clears it when it leaves an error in place, so the error is not wiped a moment later.
  const pushed = useRef(false);
  useEffect(() => {
    if (!busy) {
      startedAt.current = 0;
      if (pushed.current) setStatus(null);
      pushed.current = false;
      return;
    }
    // A sync we started is timed from the click; one we merely noticed is timed from the moment we noticed
    // it, which is the most honest number available - the shell does not say when it began.
    if (startedAt.current === 0) startedAt.current = Date.now();

    const show = () => {
      // Our own run has already delivered its verdict, so stop narrating. The shell-phase half of `busy` can
      // outlive the run - a shell that announced work and then wedged is still "working" as far as this hook
      // knows - and without this guard the next tick painted "Syncing calendar 150s" straight over the timeout
      // error just written, then cleared it a second later when the phase went stale. The user was left with
      // a message that explained nothing and then vanished.
      if (reported.current) return;
      setStatus(
        t(syncStatusKey(syncing ?? "all"), {
          seconds: elapsedSeconds(startedAt.current, Date.now()),
          // Named even when it is today: a message that always states the day it is reading cannot be
          // misread, where "today" was simply wrong on every other day the user had selected.
          date: formatLongDate(dayStartIso(syncingDate.current), i18n.language),
        }),
        "progress",
        { sticky: true },
      );
      pushed.current = true;
    };
    show();
    const tick = setInterval(show, 1000);
    return () => clearInterval(tick);
  }, [busy, syncing, setStatus, t]);

  // The message must not outlive the hook that owns it. The effect above only clears its interval, so an
  // unmount mid-sync left a **sticky** progress line frozen on screen with nothing left to count it. Only ever
  // clears a line we put there ourselves, same guard as above.
  //
  // Now that this lives in the provider, the unmount it guards is leaving the workspace entirely rather than
  // switching tabs - which is the point: a tab switch no longer takes the run's state with it.
  //
  // Deliberately its own effect rather than a clause in the one above: that one's cleanup runs on every tick
  // of `busy`/`syncing`/`t`, and clearing there would blank the bar between each second. `setStatus` is
  // stable, so this cleanup runs on unmount and nowhere else.
  useEffect(
    () => () => {
      if (!pushed.current) return;
      pushed.current = false;
      setStatus(null);
    },
    [setStatus],
  );

  const sync = useCallback(
    (scope: CalendarSyncScope, date?: string) => {
      // One at a time - the shell can only read one window anyway. `busy` rather than `syncing`, so a click
      // landing during the launch sync is ignored here instead of being sent to a shell that will refuse it.
      if (busy) return;
      startedAt.current = Date.now();
      reported.current = false; // a new run narrates again
      setSyncing(scope);
      // Held for the ticking status line, which names the day being read. Kept in a ref rather than state
      // because the ticker reads it every second and re-rendering the whole toolbar for that would be waste.
      syncingDate.current = scope === "today" ? date : undefined;

      void runCalendarSync(scope, {
        outlook,
        syncOutlookNow,
        onOutlookState,
        // `refetchType: "all"` rather than the default: the Calendar tab unmounts when you switch away, so its
        // query is often inactive - and an inactive query would be marked stale and silently not refetched,
        // leaving the run "finished" before the data it fetched existed.
        //
        // The recordings go with them. The Calendar tab draws recordings alongside the meetings, and its
        // generic Refresh button - which is what used to re-read them - is no longer offered there, so a sync
        // is now the one control that refreshes everything the day grid shows. Only the events are awaited:
        // the recordings are a background top-up, and blocking the "syncing" message on them would make the
        // calendar's own refresh look slower than it is.
        refetchEvents: () => {
          void qc.invalidateQueries({ queryKey: ["recordings"] });
          return qc.invalidateQueries({ queryKey: ["calendar-events"], refetchType: "all" });
        },
      }, syncingDate.current)
        .then(({ outlookReason }) => {
          const failure = syncErrorKey(outlookReason);
          setStatus(failure ? t(failure) : null, failure ? "error" : "info");
          // Either way we no longer own a progress line: an error is left standing for the user to read
          // (clearing it a moment later, when `busy` drops, is how the first version lost its own message),
          // and a success has just been cleared.
          pushed.current = false;
          // This run has said its piece. Nothing may narrate over it until the shell reports something new.
          reported.current = true;
        })
        .finally(() => setSyncing(null));
    },
    [busy, outlook, qc, setStatus, t],
  );

  // Memoised so the ticking status line does not re-render every consumer once a second. This provider reads
  // the status context itself (to write that line), so it re-renders on each tick whether anything it exposes
  // changed or not.
  return useMemo(() => ({ syncing, busy, sync }), [syncing, busy, sync]);
}

/// No default. A consumer outside the provider is a wiring mistake, and the benign-looking alternative - two
/// permanently-enabled sync buttons that quietly do nothing - is exactly the failure this whole change exists
/// to remove, only harder to notice.
const CalendarSyncContext = createContext<CalendarSyncValue | null>(null);

/// Owns the calendar sync for the whole workspace.
///
/// It lives here, above everything, because the run outlives any one view of it. The state used to sit in
/// `ListToolbar`, which unmounts on the Actions tab and when the left panel is collapsed - so the counter
/// restarted, a finished run could write over a message a newer instance owned, and the toolbar had to carry a
/// dedicated effect to stop a sticky progress line freezing on screen behind it. Mounted beside
/// `OutlookSyncBridge`, so it is listening before the bridge's `outlook:ready` can license the launch sync.
export function CalendarSyncProvider({ children }: { children: ReactNode }) {
  return <CalendarSyncContext.Provider value={useCalendarSyncState()}>{children}</CalendarSyncContext.Provider>;
}

export function useCalendarSync(): CalendarSyncValue {
  const value = useContext(CalendarSyncContext);
  if (!value) throw new Error("useCalendarSync must be used inside a CalendarSyncProvider");
  return value;
}
