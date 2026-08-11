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
    let sawWork = false;

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
      if (state.phase !== "idle") sawWork = true;
      else if (sawWork) finish();
    });
    const timer = setTimeout(() => finish("timeout"), deps.timeoutMs ?? SHELL_TIMEOUT_MS);

    void deps
      .syncOutlookNow({ scope })
      .then(({ started, reason }) => {
        if (!started) finish(reason ?? "error");
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

  // The count-up. A ticking message is the whole point of it: a 30-second sync with a static label is
  // indistinguishable from one that has hung. Started at 0s immediately so the bar reacts to the click, not to
  // the first tick a second later.
  useEffect(() => {
    if (!syncing) return;
    const show = () =>
      setStatus(t(syncStatusKey(syncing), { seconds: elapsedSeconds(startedAt.current, Date.now()) }), "progress", {
        sticky: true,
      });
    show();
    const tick = setInterval(show, 1000);
    return () => clearInterval(tick);
  }, [syncing, setStatus, t]);

  const sync = useCallback(
    (scope: CalendarSyncScope) => {
      if (syncing) return; // one at a time: the shell can only read one window anyway
      startedAt.current = Date.now();
      setSyncing(scope);

      void runCalendarSync(scope, {
        outlook,
        syncOutlookNow,
        onOutlookState,
        // `refetchType: "all"` rather than the default: the Calendar tab unmounts when you switch away, so its
        // query is often inactive - and an inactive query would be marked stale and silently not refetched,
        // leaving the run "finished" before the data it fetched existed.
        refetchEvents: () => qc.invalidateQueries({ queryKey: ["calendar-events"], refetchType: "all" }),
      })
        .then(({ outlookReason }) => {
          // A cooldown is not worth a red line - it means a sync just ran, so the calendar is fresh anyway.
          if (outlookReason && outlookReason !== "cooldown") setStatus(t("calSyncFailed"), "error");
          else setStatus(null);
        })
        .finally(() => setSyncing(null));
    },
    [syncing, outlook, qc, setStatus, t],
  );

  return { syncing, sync };
}
