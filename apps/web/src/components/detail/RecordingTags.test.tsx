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
        <RecordingTags
          recordingId="rec-1"
          tags={["metadata"]}
          suggested={["templates"]}
          canEdit
          {...props}
        />
      </StatusProvider>
    </QueryClientProvider>,
  );
  return qc;
}

/// The shape the container patches: the slice of `RecordingDetail` that tagging touches.
interface Detail {
  id: string;
  tags: string[];
  suggestedTags: string[];
}

/// Mounts the control the way the app does, so an optimistic patch can actually be seen: the container
/// patches the ["recording", id] cache entry, and the detail query feeds that entry back down as
/// `tags`/`suggested` props. `queryFn` decides whether a refetch ever answers - by default it hands back a
/// promise that never settles (the real `GET /api/recordings/{id}` is slow on a long recording - it carries
/// every segment), so the only thing that can put a chip on screen in these tests is the optimistic patch.
function renderLive(
  detail: Partial<Detail> = {},
  queryFn: () => Promise<Detail> = () => new Promise<Detail>(() => {}),
) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  qc.setQueryData<Detail>(["recording", "rec-1"], { id: "rec-1", tags: [], suggestedTags: [], ...detail });

  function Live() {
    const { data } = useQuery({ queryKey: ["recording", "rec-1"], queryFn });
    return (
      <RecordingTags
        recordingId="rec-1"
        tags={data?.tags ?? []}
        suggested={data?.suggestedTags ?? []}
        canEdit
      />
    );
  }
  render(
    <QueryClientProvider client={qc}>
      <StatusProvider>
        <StatusProbe />
        <Live />
      </StatusProvider>
    </QueryClientProvider>,
  );
  return qc;
}

/// A tag mock whose promise the test settles by hand, so a mutation can be held in flight across
/// assertions - the only way to observe the optimistic state, and to control the order two edits fail in.
function held() {
  let reject!: (e: unknown) => void;
  const promise = new Promise<void>((_, rj) => {
    reject = rj;
  });
  return { request: () => promise, fail: (e: unknown) => reject(e) };
}

/// The adopted chips, in the order they are drawn. Each chip is a span holding the tag text plus its remove
/// button (icon only, no text of its own), so the button's parent reads back as exactly the tag. Order
/// matters here: rolling a failed remove back has to put the tag where it was, not on the end.
const chipOrder = () =>
  screen.getAllByRole("button", { name: "Remove tag" }).map((b) => b.parentElement?.textContent);

/// The hint chips, in the order they are drawn - read off each hint's "add" half, whose text is the tag.
const hintOrder = () => screen.getAllByTitle("Add this tag").map((b) => b.textContent);

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

  it("shows a typed tag's chip immediately, without waiting for the detail refetch", async () => {
    renderLive();
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    // The detail query in this harness never answers, so this chip can only have come from the patch the
    // container wrote into the cache in `onMutate`.
    await waitFor(() => expect(screen.getByText("licensing")).toBeTruthy());
    expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("1");
    expect(api.addRecordingTag).toHaveBeenCalledWith("rec-1", "licensing");
  });

  it("rolls a failed add's chip back off the screen", async () => {
    const alpha = held();
    vi.mocked(api.addRecordingTag).mockImplementationOnce(alpha.request);
    renderLive();
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");
    await waitFor(() => expect(screen.getByText("licensing")).toBeTruthy());

    await act(async () => alpha.fail(new Error("boom")));

    // Rolled back from the snapshot `onMutate` took - not inferred from what the server now says, which is
    // unchanged by a failed request and so can never tell the two apart.
    await waitFor(() => expect(screen.queryByText("licensing")).toBeNull());
    expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("0");
  });

  it("moves a promoted hint out of the hint list immediately", async () => {
    renderLive({ suggestedTags: ["templates"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getByRole("button", { name: /templates/ }));

    // Adopting a hint has to move it in the same beat: it becomes a chip and stops being a hint.
    await waitFor(() => expect(screen.getByText("All suggestions dealt with.")).toBeTruthy());
    expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("1");
    expect(api.addRecordingTag).toHaveBeenCalledWith("rec-1", "templates");
  });

  it("takes a removed chip and a dismissed hint off the screen immediately", async () => {
    // The other two patches, on the same never-answering harness: what disappears here can only have
    // disappeared because the cache was patched.
    renderLive({ tags: ["metadata"], suggestedTags: ["templates"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getByRole("button", { name: "Remove tag" }));
    await waitFor(() => expect(screen.queryByText("metadata")).toBeNull());
    expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("0");

    await userEvent.click(screen.getByRole("button", { name: "Never suggest this" }));
    await waitFor(() => expect(screen.getByText("All suggestions dealt with.")).toBeTruthy());
  });

  // The three tests below cover the rollback's reinsert branch - putting back what a patch struck out. It is
  // the half of the undo that a failed remove, a failed dismiss, and a failed promotion all depend on, and
  // each asserts the *position* it came back at, not merely that it came back.

  it("puts a failed remove's chip back where it was", async () => {
    const beta = held();
    vi.mocked(api.removeRecordingTag).mockImplementationOnce(beta.request);
    renderLive({ tags: ["alpha", "beta", "gamma"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getAllByRole("button", { name: "Remove tag" })[1]);
    await waitFor(() => expect(chipOrder()).toEqual(["alpha", "gamma"]));

    await act(async () => beta.fail(new Error("boom")));

    // Back in the middle, not appended on the end - the chips are in adoption order and a rollback is not
    // an adoption.
    await waitFor(() => expect(chipOrder()).toEqual(["alpha", "beta", "gamma"]));
  });

  it("puts a failed dismissal's hint back where it was", async () => {
    const two = held();
    vi.mocked(api.dismissRecordingTag).mockImplementationOnce(two.request);
    renderLive({ suggestedTags: ["one", "two", "three"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getAllByRole("button", { name: "Never suggest this" })[1]);
    await waitFor(() => expect(hintOrder()).toEqual(["one", "three"]));

    await act(async () => two.fail(new Error("boom")));

    // Hints are ordered heaviest first, so the position carries meaning here too.
    await waitFor(() => expect(hintOrder()).toEqual(["one", "two", "three"]));
  });

  it("puts a failed promotion's hint back, and takes its chip away", async () => {
    // Promoting a hint moves it: the add patch both appends a chip and strikes the hint out. Undoing it has
    // to reverse both halves, which is the only case where one rollback runs `unadded` and `restored`.
    const templates = held();
    vi.mocked(api.addRecordingTag).mockImplementationOnce(templates.request);
    renderLive({ suggestedTags: ["alpha", "templates", "beta"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getByRole("button", { name: /templates/ }));
    await waitFor(() => expect(chipOrder()).toEqual(["templates"]));
    expect(hintOrder()).toEqual(["alpha", "beta"]);

    await act(async () => templates.fail(new Error("boom")));

    await waitFor(() => expect(hintOrder()).toEqual(["alpha", "templates", "beta"]));
    expect(screen.queryByRole("button", { name: "Remove tag" })).toBeNull();
    expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("0");
  });

  it("keeps a later edit's chip when an earlier overlapping edit fails", async () => {
    // Space commits a word and keeps focus, so several tags typed in a run are the intended usage. The
    // caveat of snapshot rollback is that alpha's snapshot predates beta's patch, so restoring it wholesale
    // would take beta's perfectly good chip down with alpha's.
    const alpha = held();
    vi.mocked(api.addRecordingTag)
      .mockImplementationOnce(alpha.request)
      .mockImplementationOnce(async () => undefined);
    renderLive();
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "alpha beta ");
    await waitFor(() => expect(screen.getByText("alpha")).toBeTruthy());
    expect(screen.getByText("beta")).toBeTruthy();

    await act(async () => alpha.fail(new Error("boom")));

    await waitFor(() => expect(screen.queryByText("alpha")).toBeNull());
    expect(screen.getByText("beta")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Tags" }).textContent).toContain("1");
  });

  it("does not resurrect a tag that was added and then removed", async () => {
    // The same tag added and then removed is the one sequence a locally-replayed optimistic overlay could
    // not get right: both edits are about the same chip, so reconciling them against the server's list can
    // drop the wrong one and bring back a chip the user explicitly removed. Rolling back to a snapshot never
    // asks that question, and the invalidated refetch has the last word - this test pins that.
    //
    // Unlike the tests above, this harness lets the refetch answer: the mocks mutate a stand-in server list
    // that the query hands back, so the whole loop runs - patch, request, invalidate, refetch, settle.
    let server: string[] = [];
    vi.mocked(api.addRecordingTag).mockImplementation(async (_id, tag) => {
      server = [...server, tag];
    });
    vi.mocked(api.removeRecordingTag).mockImplementation(async (_id, tag) => {
      server = server.filter((x) => x !== tag);
    });

    const qc = renderLive({}, async () => ({ id: "rec-1", tags: [...server], suggestedTags: [] }));

    await userEvent.click(screen.getByRole("button", { name: "Tags" }));
    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

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

  it("wires canEdit=false through to a read-only popover: no control can trigger a mutation, and the pill still shows the count", async () => {
    renderTags({ tags: ["metadata", "licensing"], suggested: ["templates"], canEdit: false });
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill.textContent).toContain("2");

    await userEvent.click(pill);

    // The chips are still visible as plain text...
    expect(screen.getByText("metadata")).toBeTruthy();
    expect(screen.getByText("licensing")).toBeTruthy();
    // ...but nothing in the popover can drive a mutation: no entry field, no remove buttons, and the
    // suggestions section (which is where "add"/"dismiss" live) is gone entirely.
    expect(screen.queryByLabelText("Add a tag")).toBeNull();
    expect(screen.queryByRole("button", { name: "Remove tag" })).toBeNull();
    expect(screen.queryByText("templates")).toBeNull();

    expect(api.addRecordingTag).not.toHaveBeenCalled();
    expect(api.removeRecordingTag).not.toHaveBeenCalled();
    expect(api.dismissRecordingTag).not.toHaveBeenCalled();
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
          {mounted && <RecordingTags recordingId="rec-1" tags={[]} suggested={[]} canEdit />}
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
