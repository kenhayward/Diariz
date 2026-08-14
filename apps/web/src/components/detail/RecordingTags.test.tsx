import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
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
  beforeEach(() => {
    vi.clearAllMocks();
    // clearAllMocks drops the resolved values set in the factory too, so restore the default happy path.
    vi.mocked(api.addRecordingTag).mockResolvedValue(undefined);
    vi.mocked(api.removeRecordingTag).mockResolvedValue(undefined);
    vi.mocked(api.dismissRecordingTag).mockResolvedValue(undefined);
  });

  it("shows the adopted tag count on the pill and opens the popover on click", async () => {
    renderTags({ tags: ["metadata", "licensing"] });
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill.textContent).toContain("2");
    expect(screen.queryByLabelText("Add a tag")).toBeNull();

    await userEvent.click(pill);

    expect(screen.getByLabelText("Add a tag")).toBeTruthy();
    // The chips come straight from the props, not from any local copy of them.
    expect(screen.getByText("metadata")).toBeTruthy();
    expect(screen.getByText("licensing")).toBeTruthy();
  });

  it("sends a typed tag to the API", async () => {
    renderTags({ tags: [] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    await waitFor(() => expect(api.addRecordingTag).toHaveBeenCalledWith("rec-1", "licensing"));
  });

  it("promotes a suggestion by adding it", async () => {
    renderTags({ tags: [], suggested: ["templates"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getByRole("button", { name: /templates/ }));

    await waitFor(() => expect(api.addRecordingTag).toHaveBeenCalledWith("rec-1", "templates"));
    // Adopting a suggestion is an add, never a dismissal - the two sit next to each other on the same chip.
    expect(api.dismissRecordingTag).not.toHaveBeenCalled();
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

  it("does not resurrect a tag that was added and then removed", async () => {
    // The same tag added and then removed is the one sequence a locally-replayed optimistic overlay could
    // not get right: both edits are about the same chip, so reconciling them against the server's list can
    // drop the wrong one and bring back a chip the user explicitly removed. With the chips rendering only
    // from what the server reports, the sequence has nowhere to go wrong - this test pins that.
    //
    // The harness closes the real loop rather than pinning fixed props: the mocks mutate a stand-in server
    // list, and a `useQuery` on the ["recording", id] key - the key the container invalidates - feeds it
    // back down as props, the way the recording detail query does in the app.
    let server: string[] = [];
    vi.mocked(api.addRecordingTag).mockImplementation(async (_id, tag) => {
      server = [...server, tag];
    });
    vi.mocked(api.removeRecordingTag).mockImplementation(async (_id, tag) => {
      server = server.filter((x) => x !== tag);
    });

    function Harness() {
      const { data } = useQuery({ queryKey: ["recording", "rec-1"], queryFn: async () => [...server] });
      return <RecordingTags recordingId="rec-1" tags={data ?? []} suggested={[]} />;
    }
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <StatusProvider>
          <StatusProbe />
          <Harness />
        </StatusProvider>
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Tags" }));
    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    // The chip appears once the server has it - the refetch the add's invalidate triggered.
    await waitFor(() => expect(screen.getByText("licensing")).toBeTruthy());

    await userEvent.click(screen.getByRole("button", { name: "Remove tag" }));

    await waitFor(() => expect(screen.queryByText("licensing")).toBeNull());
    expect(api.addRecordingTag).toHaveBeenCalledWith("rec-1", "licensing");
    expect(api.removeRecordingTag).toHaveBeenCalledWith("rec-1", "licensing");
    expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("0");

    // ...and it stays gone. Force one more read of the server list, so a chip that was only waiting on
    // another refetch to reappear would have its chance here.
    await act(async () => {
      await qc.refetchQueries({ queryKey: ["recording", "rec-1"] });
    });
    expect(screen.queryByText("licensing")).toBeNull();
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
