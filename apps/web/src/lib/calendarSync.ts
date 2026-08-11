import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
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
  syncOutlookNow: (options: { scope: CalendarSyncScope }) => Promise<{ started: boolean; reason?: string }>;
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
  return scope === "today" ? "statusSyncingCalendarToday" : "statusSyncingCalendar";
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
): Promise<CalendarSyncResult> {
  const result: CalendarSyncResult = {};

  if (deps.outlook) {
    const reason = await syncOutlookAndWait(scope, deps);
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
function syncOutlookAndWait(scope: CalendarSyncScope, deps: CalendarSyncDeps): Promise<string | undefined> {
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
      .syncOutlookNow({ scope })
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
///
/// Mounted by the toolbar rather than the Calendar tab, because the buttons live there now - and because the
/// toolbar stays mounted across tab switches, so a sync started from the Calendar keeps counting if the user
/// wanders off to the list while they wait.
export function useCalendarSync(): {
  /// The scope of the run in progress, or null when idle.
  syncing: CalendarSyncScope | null;
  /// Whether **any** sync is under way, including one this app did not start - the shell's launch sync, or one
  /// from the tray. The buttons disable on this, not on `syncing`.
  busy: boolean;
  /// Start a run. Ignored while one is already going; the same two buttons work in a browser, where a sync is
  /// simply Google and the feeds with no shell to ask.
  sync: (scope: CalendarSyncScope) => void;
} {
  const { t } = useTranslation("workspace");
  const qc = useQueryClient();
  const { setStatus } = useStatus();
  const [syncing, setSyncing] = useState<CalendarSyncScope | null>(null);
  const startedAt = useRef(0);

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
  useEffect(() => onOutlookState((s) => setShellPhase(s.phase)), []);

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
      setStatus(
        t(syncStatusKey(syncing ?? "all"), { seconds: elapsedSeconds(startedAt.current, Date.now()) }),
        "progress",
        { sticky: true },
      );
      pushed.current = true;
    };
    show();
    const tick = setInterval(show, 1000);
    return () => clearInterval(tick);
  }, [busy, syncing, setStatus, t]);

  // The message must not outlive the hook that owns it. The effect above only clears its interval, so a
  // toolbar unmounted mid-sync - switching to the Actions tab, which swaps this toolbar out, or collapsing the
  // left panel - left a **sticky** progress line frozen on screen with nothing left to count it, and a
  // remounted toolbar would not clear it either: `pushed` is per-instance, and the new instance's copy
  // correctly says it wrote nothing. Only ever clears a line we put there ourselves, same guard as above.
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
    (scope: CalendarSyncScope) => {
      // One at a time - the shell can only read one window anyway. `busy` rather than `syncing`, so a click
      // landing during the launch sync is ignored here instead of being sent to a shell that will refuse it.
      if (busy) return;
      startedAt.current = Date.now();
      setSyncing(scope);

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
          return qc.invalidateQueries({ queryKey: ["calendar-events"], refetchType: "all" });
        },
      })
        .then(({ outlookReason }) => {
          const failure = syncErrorKey(outlookReason);
          setStatus(failure ? t(failure) : null, failure ? "error" : "info");
          // Either way we no longer own a progress line: an error is left standing for the user to read
          // (clearing it a moment later, when `busy` drops, is how the first version lost its own message),
          // and a success has just been cleared.
          pushed.current = false;
        })
        .finally(() => setSyncing(null));
    },
    [busy, outlook, qc, setStatus, t],
  );

  return { syncing, busy, sync };
}
