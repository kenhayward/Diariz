import { render, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { useLiveNotes, type LiveNotes } from "./useLiveNotes";

vi.mock("./pendingNotes", () => ({
  savePendingNotes: vi.fn().mockResolvedValue(undefined),
  clearPendingNotes: vi.fn().mockResolvedValue(undefined),
}));
import { savePendingNotes, clearPendingNotes } from "./pendingNotes";

/// The recorded clock the hook stamps with. A plain counter, so a test can assert the hook asked the
/// caller for the time rather than reading a clock of its own.
let stamp = 0;
let api: LiveNotes;

function Harness({ userId = "u1" }: { userId?: string | null }) {
  api = useLiveNotes({ userId, stampMs: () => stamp });
  return <div data-testid="lines">{api.lines.map((l) => `${l.text}@${l.capturedAtMs}`).join("|")}</div>;
}

const rendered = () => document.querySelector('[data-testid="lines"]')?.textContent ?? "";

beforeEach(() => {
  vi.clearAllMocks();
  stamp = 0;
});

describe("useLiveNotes", () => {
  it("stamps a new line from the caller's clock, not one of its own", () => {
    render(<Harness />);

    stamp = 61_000;
    act(() => api.add("first"));

    expect(rendered()).toBe("first@61000");
  });

  it("numbers lines in the order they were committed", () => {
    render(<Harness />);
    act(() => api.add("one"));
    act(() => api.add("two"));

    expect(api.snapshot().map((l) => l.ordinal)).toEqual([0, 1]);
  });

  it("mirrors every change to the durable stash, unattached", () => {
    render(<Harness />);

    stamp = 5_000;
    act(() => api.add("mirror me"));

    expect(savePendingNotes).toHaveBeenCalledWith(
      expect.objectContaining({
        userId: "u1",
        recordingId: null,
        lines: [{ text: "mirror me", capturedAtMs: 5_000 }],
      }),
    );
  });

  it("edits a line in place, leaving its stamp alone", () => {
    render(<Harness />);
    stamp = 1_000;
    act(() => api.add("draft"));
    const id = api.snapshot()[0].id;

    stamp = 99_000; // an edit must not re-stamp
    act(() => api.edit(id, "final"));

    expect(rendered()).toBe("final@1000");
  });

  it("deletes the named line and keeps the rest", () => {
    render(<Harness />);
    act(() => api.add("keep"));
    act(() => api.add("drop"));
    const dropId = api.snapshot()[1].id;

    act(() => api.remove(dropId));

    expect(api.snapshot().map((l) => l.text)).toEqual(["keep"]);
  });

  it("ignores an edit or delete for an id it does not hold", () => {
    render(<Harness />);
    act(() => api.add("only"));

    act(() => api.edit("nope", "changed"));
    act(() => api.remove("nope"));

    expect(api.snapshot().map((l) => l.text)).toEqual(["only"]);
  });

  // upload() reads the lines after its first await, when React state may not have flushed - so the
  // snapshot has to be the live value, not the last rendered one.
  it("snapshot is current before React has re-rendered", () => {
    render(<Harness />);

    act(() => {
      api.add("a");
      expect(api.snapshot().map((l) => l.text)).toEqual(["a"]);
    });
  });

  it("reset clears the lines and the durable stash", async () => {
    render(<Harness />);
    act(() => api.add("gone"));

    await act(async () => {
      await api.reset();
    });

    expect(rendered()).toBe("");
    expect(api.snapshot()).toEqual([]);
    expect(clearPendingNotes).toHaveBeenCalledWith("u1");
  });

  it("degrades to memory-only with no signed-in user", async () => {
    render(<Harness userId={null} />);

    act(() => api.add("anonymous"));
    expect(api.snapshot().map((l) => l.text)).toEqual(["anonymous"]);
    expect(savePendingNotes).not.toHaveBeenCalled();

    await act(async () => {
      await api.reset();
    });
    expect(clearPendingNotes).not.toHaveBeenCalled();
  });
});
