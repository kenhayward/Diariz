import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { StatusProvider, useStatus } from "../../lib/status";
import RecordingTags from "./RecordingTags";

vi.mock("../../lib/api", () => ({
  api: {
    addRecordingTag: vi.fn().mockResolvedValue(undefined),
    removeRecordingTag: vi.fn().mockResolvedValue(undefined),
    dismissRecordingTag: vi.fn().mockResolvedValue(undefined),
  },
  // A rejected mutation reports through this - the container calls apiErrorMessage(e, fallback) and pushes
  // the result to the shared status bar. The fallback is all this suite needs back.
  apiErrorMessage: (_e: unknown, fallback: string) => fallback,
}));

import { api } from "../../lib/api";

/// Renders the current global status message, so a test can see what the container pushed to (and
/// retracted from) the shared status bar. Mirrors the same probe in `pages/RecordingDetail.test.tsx`.
function StatusProbe() {
  const { status } = useStatus();
  return <div data-testid="status">{status?.text ?? ""}</div>;
}

function renderTags(props: Partial<ComponentProps<typeof RecordingTags>> = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <StatusProvider>
        <StatusProbe />
        <RecordingTags recordingId="rec-1" tags={["metadata"]} suggested={["templates"]} {...props} />
      </StatusProvider>
    </QueryClientProvider>,
  );
  return qc;
}

describe("RecordingTags", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows the adopted tag count on the pill and opens the popover on click", async () => {
    renderTags({ tags: ["metadata", "licensing"] });
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill.textContent).toContain("2");
    expect(screen.queryByLabelText("Add a tag")).toBeNull();

    await userEvent.click(pill);

    expect(screen.getByLabelText("Add a tag")).toBeTruthy();
  });

  it("sends a typed tag to the API", async () => {
    renderTags({ tags: [] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    await waitFor(() => expect(api.addRecordingTag).toHaveBeenCalledWith("rec-1", "licensing"));
  });

  it("shows a typed tag immediately, before the server answers", async () => {
    renderTags({ tags: [] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("1");
  });

  it("promotes a suggestion, moving it out of the hint list", async () => {
    renderTags({ tags: [], suggested: ["templates"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getByRole("button", { name: /templates/ }));

    await waitFor(() => expect(api.addRecordingTag).toHaveBeenCalledWith("rec-1", "templates"));
    expect(screen.getByText("All suggestions dealt with.")).toBeTruthy();
  });

  it("removes an adopted tag", async () => {
    renderTags({ tags: ["metadata"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getByRole("button", { name: "Remove tag" }));

    await waitFor(() => expect(api.removeRecordingTag).toHaveBeenCalledWith("rec-1", "metadata"));
  });

  it("dismisses a suggestion", async () => {
    renderTags({ suggested: ["templates"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getByRole("button", { name: "Never suggest this" }));

    await waitFor(() => expect(api.dismissRecordingTag).toHaveBeenCalledWith("rec-1", "templates"));
    expect(api.removeRecordingTag).not.toHaveBeenCalled();
  });

  it("invalidates the tag cloud after a change, so the Tags tab keeps up", async () => {
    const qc = renderTags({ tags: [] });
    const invalidate = vi.spyOn(qc, "invalidateQueries");
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith(expect.objectContaining({ queryKey: ["tags"] })),
    );
  });

  it("clears the optimistic chip when the add request fails, rather than leaving it stuck", async () => {
    vi.mocked(api.addRecordingTag).mockRejectedValueOnce(new Error("boom"));
    renderTags({ tags: [] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    // The server never applied it, so nothing about `tags`/`suggested` ever changes - only the mutation's
    // own failure can clear the overlay. Without that, this never resolves and the test times out.
    await waitFor(() => expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("0"));
  });

  it("lets fresh server tags win over the optimistic overlay once they actually differ", async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { rerender } = render(
      <QueryClientProvider client={qc}>
        <RecordingTags recordingId="rec-1" tags={[]} suggested={[]} />
      </QueryClientProvider>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));
    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");
    expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("1");

    // Simulates the detail query actually refetching with the server's own (now-updated) lists - the
    // overlay must defer to this, not keep showing its own guess forever.
    rerender(
      <QueryClientProvider client={qc}>
        <RecordingTags recordingId="rec-1" tags={["licensing", "extra"]} suggested={[]} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("2"));
  });

  it("keeps a still-valid chip visible when an earlier overlapping edit fails", async () => {
    // The popover keeps focus after a word commits, so typing several tags in a run is the intended flow -
    // overlapping requests are the normal case, not a corner case. Alpha's request is held open; beta's
    // resolves right away.
    let rejectAlpha!: (e: unknown) => void;
    const alphaPromise = new Promise<void>((_, reject) => {
      rejectAlpha = reject;
    });
    vi.mocked(api.addRecordingTag)
      .mockImplementationOnce(() => alphaPromise)
      .mockImplementationOnce(() => Promise.resolve(undefined));

    renderTags({ tags: [] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "alpha beta ");

    await waitFor(() => expect(api.addRecordingTag).toHaveBeenCalledTimes(2));
    expect(screen.getByText("alpha")).toBeTruthy();
    expect(screen.getByText("beta")).toBeTruthy();

    // Alpha's request now fails. Retracting it must not take beta - a different, still-valid edit - down
    // with it.
    rejectAlpha(new Error("boom"));

    await waitFor(() => expect(screen.queryByText("alpha")).toBeNull());
    expect(screen.getByText("beta")).toBeTruthy();
  });

  it("shows a failure in the shared status bar, and retracts it once a later edit succeeds", async () => {
    vi.mocked(api.addRecordingTag).mockRejectedValueOnce(new Error("boom"));
    renderTags({ tags: [] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    await waitFor(() =>
      expect(screen.getByTestId("status").textContent).toBe("Could not save the tag change."),
    );

    // A later edit goes through - the earlier failure must not linger over it (the "error" tone is sticky
    // and never auto-clears on its own; only this component retracting it clears the bar).
    await userEvent.type(screen.getByLabelText("Add a tag"), "metadata ");

    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe(""));
  });

  it("retracts its failure message from the status bar when it unmounts", async () => {
    vi.mocked(api.addRecordingTag).mockRejectedValueOnce(new Error("boom"));
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    // Mirrors how the real app mounts this control: RecordingTags can disappear (navigating off the
    // recording) while the rest of the app - and its shared status bar - stays up.
    function Harness() {
      const [mounted, setMounted] = useState(true);
      return (
        <StatusProvider>
          <StatusProbe />
          <button onClick={() => setMounted(false)}>unmount</button>
          {mounted && <RecordingTags recordingId="rec-1" tags={[]} suggested={[]} />}
        </StatusProvider>
      );
    }
    render(
      <QueryClientProvider client={qc}>
        <Harness />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Tags" }));
    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");
    await waitFor(() => expect(screen.getByTestId("status").textContent).not.toBe(""));

    await userEvent.click(screen.getByRole("button", { name: "unmount" }));

    expect(screen.getByTestId("status").textContent).toBe("");
  });
});
