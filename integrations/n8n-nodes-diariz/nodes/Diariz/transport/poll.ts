export type TerminalState = "ready" | "failed" | "pending";

export interface PollOptions {
  intervalMs: number;
  timeoutMs: number;
  /// Injectable so the tests do not actually wait.
  sleep?: (ms: number) => Promise<void>;
  now?: () => number;
}

const wait = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/// Polls an asynchronous Diariz job until it reaches a terminal state. Diariz answers a formula run with 202
/// and a Generating document, but a workflow author almost always wants the finished text in the same node,
/// so the operation polls here rather than making them build a wait loop.
export async function pollUntilTerminal<T>(
  fetch: () => Promise<T>,
  classify: (value: T) => TerminalState,
  options: PollOptions,
): Promise<T> {
  const sleep = options.sleep ?? wait;
  const now = options.now ?? (() => Date.now());
  const deadline = now() + options.timeoutMs;

  for (;;) {
    const value = await fetch();
    const state = classify(value);

    if (state === "ready") return value;
    if (state === "failed") {
      throw new Error((value as { error?: string })?.error ?? "The Diariz job failed.");
    }
    if (now() >= deadline) {
      throw new Error(
        `Timed out waiting for Diariz to finish - it is still generating. It may complete later, so fetch the result by ID, or turn off "Wait for Completion" and use a Diariz Trigger on the completion event instead.`,
      );
    }
    await sleep(options.intervalMs);
  }
}
