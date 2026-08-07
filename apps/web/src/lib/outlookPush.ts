/// Uploading a window the desktop shell harvested. Split out from the bridge component so the paging and
/// failure rules are testable without rendering anything.

import { chunkEvents, type OutlookPushPayload, type OutlookPushResult } from "./outlookSync";
import type { OutlookSyncRequest, OutlookSyncResult } from "./types";

/// Events per request. Comfortably under the server's 500-per-page ceiling, so a slightly generous client
/// still succeeds rather than 413-ing mid-run.
export const PAGE_SIZE = 250;

/// The slice of the API client this needs, injected so tests can supply a fake.
export interface OutlookPushClient {
  pushOutlookEvents(req: OutlookSyncRequest): Promise<OutlookSyncResult>;
}

/// Upload a harvested window, one page at a time.
///
/// `final` is set on the **last page only**, and the payload's `complete` is passed straight through: together
/// those two are what license the server to delete meetings it was not told about. So a run that fails partway
/// must stop rather than carry on - sending a later page after a gap would let the server treat the missing
/// events as cancellations. Hence the abort on first failure.
export async function pushWindow(
  payload: OutlookPushPayload,
  client: OutlookPushClient,
): Promise<OutlookPushResult> {
  const pages = chunkEvents(payload.events, PAGE_SIZE);
  let created = 0;
  let updated = 0;
  let deleted = 0;
  let eventCount = 0;

  for (let i = 0; i < pages.length; i++) {
    const req: OutlookSyncRequest = {
      syncId: payload.syncId,
      device: payload.device,
      windowStart: payload.windowStart,
      windowEnd: payload.windowEnd,
      events: pages[i],
      complete: payload.complete,
      pageIndex: i,
      final: i === pages.length - 1,
    };

    try {
      const result = await client.pushOutlookEvents(req);
      created += result.created;
      updated += result.updated;
      deleted += result.deleted;
      eventCount = result.eventCount;
    } catch (e) {
      // Stop here. The run never reaches its final page, so the server leaves the mirror alone rather than
      // sweeping against a half-delivered window.
      return { ok: false, error: e instanceof Error ? e.message : String(e) };
    }
  }

  return { ok: true, created, updated, deleted, eventCount };
}
