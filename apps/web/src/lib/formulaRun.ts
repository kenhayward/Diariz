/// Waiting for a formula run to finish.
///
/// Running a formula is asynchronous: the API returns 202 with a `Generating` row carrying an empty body, and
/// a background worker fills it in. Anything that wants the *text* has to wait for that. The detail pages do
/// it with a react-query `refetchInterval`; chat's `/formula <name>` has no query to hang it off, so it polls
/// through this.
///
/// (The server does push a `FormulaResultStatusChanged` SignalR event on every transition, but nothing in the
/// web app subscribes to it yet - the rest of the app polls, and this follows suit rather than being the one
/// place that depends on the socket.)
///
/// Pure but for the injected `sleep`, so the loop is unit-testable without timers.

/// A result row as far as the wait cares - the shape both `FormulaResult` and `SectionFormulaResult` share.
type ResultRow = { id: string; status: string; error: string | null };

export type FormulaRunOutcome =
  | { kind: "ready" }
  /// The run failed; `error` is the reason the worker recorded (null if it recorded none).
  | { kind: "failed"; error: string | null }
  /// The row disappeared while we were waiting - deleted mid-run.
  | { kind: "gone" }
  /// Still generating when we gave up. The run is not cancelled; it will land in the Formulas tab.
  | { kind: "timeout" };

export interface AwaitOptions {
  intervalMs: number;
  timeoutMs: number;
  sleep: (ms: number) => Promise<void>;
}

/// Poll until `resultId` is no longer generating. Checks immediately (the worker may already have finished),
/// then every `intervalMs` until `timeoutMs` elapses.
export async function awaitFormulaResult(
  resultId: string,
  poll: () => Promise<ResultRow[]>,
  { intervalMs, timeoutMs, sleep }: AwaitOptions,
): Promise<FormulaRunOutcome> {
  for (let elapsed = 0; ; elapsed += intervalMs) {
    const row = (await poll()).find((r) => r.id === resultId);
    if (!row) return { kind: "gone" };
    if (row.status === "Ready") return { kind: "ready" };
    if (row.status === "Failed") return { kind: "failed", error: row.error };

    if (elapsed >= timeoutMs) return { kind: "timeout" };
    await sleep(intervalMs);
  }
}
