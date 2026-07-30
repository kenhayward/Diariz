import axios from "axios";

/// Retrying a request that failed at the proxy, so a redeploy of the API is not felt as a lost upload.
///
/// The audio itself is never at risk - `pendingRecording` stashes the blob to IndexedDB before the upload
/// is attempted - but without this a user who pressed Stop during the API's restart window got an error
/// and had to find the recovery banner. A couple of retries covers the gap silently.

/// Delays between upload attempts, in ms. Sized to cover an API container restart (tens of seconds) while
/// still surfacing a real outage rather than hanging on a spinner indefinitely.
export const UPLOAD_RETRY_DELAYS_MS = [3_000, 8_000, 20_000] as const;

/// True only for the statuses a reverse proxy returns when it could not reach the API at all.
///
/// The narrowness is the point. A 502/503/504 from nginx means the request never reached application
/// code, so replaying it cannot duplicate anything. A **bare network error is deliberately excluded**:
/// a request that got no response may still have been received and processed, and a duplicated recording
/// is a worse outcome than an error message. Anything the API itself answered (4xx, 500) would either
/// fail identically on a retry or already be a completed side effect.
export function isGatewayError(e: unknown): boolean {
  if (!axios.isAxiosError(e)) return false;
  const status = e.response?.status;
  return status === 502 || status === 503 || status === 504;
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/// Run `fn`, retrying only while it fails with a gateway error. Rethrows the last error once the delays
/// are exhausted, so the caller's existing failure path (stash + recovery banner) still runs.
export async function retryOnGatewayError<T>(
  fn: () => Promise<T>,
  opts: { delays?: readonly number[]; sleep?: (ms: number) => Promise<void> | void } = {},
): Promise<T> {
  const delays = opts.delays ?? UPLOAD_RETRY_DELAYS_MS;
  const sleep = opts.sleep ?? wait;

  for (let attempt = 0; ; attempt++) {
    try {
      return await fn();
    } catch (e) {
      if (attempt >= delays.length || !isGatewayError(e)) throw e;
      await sleep(delays[attempt]);
    }
  }
}
