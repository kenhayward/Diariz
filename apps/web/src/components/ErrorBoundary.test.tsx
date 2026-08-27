import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import ErrorBoundary from "./ErrorBoundary";

/// Throws on first render. The boundary is the only way to observe what a crash looks like to a user, so the
/// tests go through a really-throwing child rather than calling the class's internals.
function Boom({ error }: { error: Error }): never {
  throw error;
}

/// The copy RouteErrorBoundary supplies from i18n; passed explicitly here because ErrorBoundary is a class
/// component and deliberately holds no strings of its own.
const staleChunk = {
  title: "Diariz has been updated.",
  hint: "Reload to get the new version. Any recording in progress will be lost.",
  action: "Reload",
};

const STALE = new Error(
  "Failed to fetch dynamically imported module: https://app.example.com/assets/LlmModels-CcQYuFEJ.js",
);

function renderBoundary(error: Error) {
  return render(
    <ErrorBoundary
      resetKey="/x"
      message="Something went wrong showing this page."
      hint="Try opening it again."
      staleChunk={staleChunk}
    >
      <Boom error={error} />
    </ErrorBoundary>,
  );
}

describe("ErrorBoundary", () => {
  beforeEach(() => {
    // React logs the caught error, and the boundary logs its own. Neither is a test failure, but both would
    // spoil an otherwise clean run.
    vi.spyOn(console, "error").mockImplementation(() => {});
  });
  afterEach(() => vi.restoreAllMocks());

  it("shows the generic message for an ordinary crash", () => {
    renderBoundary(new Error("Cannot read properties of undefined"));

    expect(screen.getByText("Something went wrong showing this page.")).toBeTruthy();
    // No reload offer: reloading does not fix a real bug, and offering it would send the user in circles.
    expect(screen.queryByRole("button", { name: /reload/i })).toBeNull();
  });

  // The case from the bug report: the page the user asked for was deleted by a deploy while their session
  // was still running. A dead end is the wrong answer - the fix is one reload away, and only the app knows
  // that. This is the path taken when the automatic reload declined, e.g. during a recording.
  it("offers a reload, and says why, when the chunk is stale", () => {
    renderBoundary(STALE);

    expect(screen.getByText("Diariz has been updated.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reload" })).toBeTruthy();
  });

  it("reloads the page when that button is pressed", () => {
    const reload = vi.fn();
    render(
      <ErrorBoundary resetKey="/x" message="m" hint="h" staleChunk={staleChunk} reload={reload}>
        <Boom error={STALE} />
      </ErrorBoundary>,
    );

    fireEvent.click(screen.getByRole("button", { name: "Reload" }));

    expect(reload).toHaveBeenCalledTimes(1);
  });
});
