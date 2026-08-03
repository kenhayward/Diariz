import { render, screen, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { record, clearTrail } from "../lib/trail";

vi.mock("../lib/api", () => ({
  api: { submitFeedback: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import FeedbackModal from "./FeedbackModal";

describe("FeedbackModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    clearTrail();
  });

  it("submits the description together with the trail and the route", async () => {
    record({ kind: "nav", label: "/recordings/1" });
    const submit = vi.fn().mockResolvedValue({ id: "abc" });
    vi.mocked(api).submitFeedback = submit;

    render(<FeedbackModal onClose={() => {}} />);
    await userEvent.type(screen.getByRole("textbox"), "The delete button was enabled");
    await userEvent.click(screen.getByRole("button", { name: /send/i }));

    expect(submit).toHaveBeenCalledTimes(1);
    const [description, , trailJson] = submit.mock.calls[0];
    expect(description).toBe("The delete button was enabled");
    expect(trailJson).toContain("/recordings/1");
  });

  it("will not submit an empty description", async () => {
    const submit = vi.fn();
    vi.mocked(api).submitFeedback = submit;

    render(<FeedbackModal onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: /send/i }));

    expect(submit).not.toHaveBeenCalled();
  });

  it("drags by its header and keeps its dialog role", async () => {
    render(<FeedbackModal onClose={() => {}} />);
    const dialog = screen.getByRole("dialog");
    const header = screen.getByTestId("feedback-drag-handle");

    fireEvent.mouseDown(header, { clientX: 100, clientY: 100 });
    fireEvent.mouseMove(window, { clientX: 160, clientY: 140 });
    fireEvent.mouseUp(window);

    // Moved, and still a dialog - dragging must not cost the accessibility contract.
    expect(dialog.style.transform).not.toBe("");
    expect(screen.getByRole("dialog")).toBeTruthy();
  });

  it("closes on Escape after being dragged", async () => {
    const onClose = vi.fn();
    render(<FeedbackModal onClose={onClose} />);
    fireEvent.mouseDown(screen.getByTestId("feedback-drag-handle"), { clientX: 10, clientY: 10 });
    fireEvent.mouseMove(window, { clientX: 60, clientY: 60 });
    fireEvent.mouseUp(window);

    await userEvent.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalled();
  });
});
