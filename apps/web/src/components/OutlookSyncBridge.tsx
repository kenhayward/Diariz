import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { api } from "../lib/api";
import { pushWindow } from "../lib/outlookPush";
import { onOutlookPush, reportOutlookReady, reportOutlookResult } from "../lib/outlookSync";

/// Connects the desktop shell's Outlook reader to the API. Renders nothing.
///
/// The shell can read the local calendar but holds no token; this side holds the token but cannot see Outlook.
/// So the shell harvests a window, hands it over, and this uploads it - the same split the screenshot feature
/// already uses.
///
/// Mounted once, high in the tree rather than inside Preferences, because a sync fires on launch and whenever
/// the tray asks - none of which require the settings window to be open. In a plain browser every call here is
/// a no-op, so it costs nothing to mount unconditionally.
export default function OutlookSyncBridge() {
  // Both queries are cheap and already cached by the rest of the app.
  const { data: settings } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });
  const { data: sources } = useQuery({ queryKey: ["outlook-sources"], queryFn: api.listOutlookSources });

  const enabled = settings?.outlookSyncEnabled === true;
  // Read the window/privacy choices from this machine's own device row when it has one. Before the first
  // sync there is none, so fall back to the same defaults the server would apply.
  const device = sources?.[0];
  const pastDays = device?.pastDays ?? 30;
  const futureDays = device?.futureDays ?? 180;
  const skipPrivate = device?.skipPrivate ?? true;
  const includeBody = device?.includeBody ?? true;

  // Tell the shell the connector's state. This doubles as "a signed-in renderer is ready to POST", which is
  // what lets the shell run its launch sync - it cannot use app-ready for that, because the user may not be
  // signed in yet. Re-sent whenever any of it changes.
  useEffect(() => {
    reportOutlookReady({ enabled, pastDays, futureDays, skipPrivate, includeBody });
  }, [enabled, pastDays, futureDays, skipPrivate, includeBody]);

  useEffect(() => {
    return onOutlookPush(async (payload) => {
      const result = await pushWindow(payload, api);
      // Report the outcome so the tray label and its notification say what actually happened rather than
      // assuming success.
      reportOutlookResult(result);
    });
  }, []);

  return null;
}
