import { render, screen, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, it, expect, vi } from "vitest";
import type { LlmModel } from "../../lib/types";
import RoutingMatrix from "./RoutingMatrix";

const MODELS: LlmModel[] = [
  {
    id: "a", name: "gpt-oss-20b", displayName: null, description: null, apiBase: "http://a/v1", hasApiKey: true,
    contextLength: 8192, chatEnabled: false, parameters: {},
  },
  {
    id: "b", name: "qwen3-27b", displayName: null, description: null, apiBase: "http://b/v1", hasApiKey: false,
    contextLength: 200000, chatEnabled: false, parameters: {},
  },
];

function show(
  assignments: Record<string, string>,
  defaultModelId: string | null,
  onRoute = vi.fn(),
  models: LlmModel[] = MODELS,
  onChatEnabledChange = vi.fn(),
) {
  render(
    <RoutingMatrix
      models={models}
      assignments={assignments}
      defaultModelId={defaultModelId}
      onRoute={onRoute}
      onEdit={vi.fn()}
      tests={{}}
      onTest={vi.fn()}
      onTestAll={vi.fn()}
      onChatEnabledChange={onChatEnabledChange}
    />,
  );
  return onRoute;
}

/// The matrix replaces eight separate selects, and it has to keep their exact semantics. The one that is
/// easy to lose is "unassigned": a call group with no entry falls through to the platform default, which
/// is NOT the same as an entry naming the model that currently happens to be the default. Collapsing the
/// two would silently re-point a call type the next time the default changed.
describe("RoutingMatrix", () => {
  it("names every cell so it can be read without seeing the grid", () => {
    show({}, null);

    expect(screen.getByRole("radio", { name: "Summaries on qwen3-27b" })).toBeTruthy();
    expect(screen.getByRole("radio", { name: "Default on gpt-oss-20b" })).toBeTruthy();
  });

  it("routes a call type to a model, sending the whole routing object", () => {
    // The API replaces the entire set on every write, so a partial payload silently drops the rest.
    const onRoute = show({ Tags: "a" }, "a");

    fireEvent.click(screen.getByRole("radio", { name: "Summaries on qwen3-27b" }));

    expect(onRoute).toHaveBeenCalledWith({ defaultModelId: "a", assignments: { Tags: "a", Summaries: "b" } });
  });

  it("does nothing when the already-selected cell is clicked", () => {
    // A call type has to run somewhere, so there is no "unselect" - and a write here would be a pointless
    // round trip that flashes the whole grid.
    const onRoute = show({ Summaries: "b" }, "a");

    fireEvent.click(screen.getByRole("radio", { name: "Summaries on qwen3-27b" }));

    expect(onRoute).not.toHaveBeenCalled();
  });

  it("sets the platform default from the Default column", () => {
    const onRoute = show({ Tags: "a" }, "a");

    fireEvent.click(screen.getByRole("radio", { name: "Default on qwen3-27b" }));

    expect(onRoute).toHaveBeenCalledWith({ defaultModelId: "b", assignments: { Tags: "a" } });
  });

  it("shows an unassigned call type in the No model row, not on the default model", () => {
    // Drawing it on the default model's row would claim an assignment that does not exist.
    show({}, "b");

    expect(screen.getByRole("radio", { name: "Summaries follows the default model", checked: true })).toBeTruthy();
    expect(screen.getByRole("radio", { name: "Summaries on qwen3-27b", checked: false })).toBeTruthy();
  });

  it("distinguishes an explicit assignment to the default model from no assignment at all", () => {
    show({ Summaries: "b" }, "b");

    expect(screen.getByRole("radio", { name: "Summaries on qwen3-27b", checked: true })).toBeTruthy();
    expect(screen.getByRole("radio", { name: "Summaries follows the default model", checked: false })).toBeTruthy();
  });

  it("returns a call type to the default by deleting its entry, not by naming the default model", () => {
    // Writing `Summaries: "b"` here would look identical today and diverge the moment the default moved.
    const onRoute = show({ Summaries: "b", Tags: "a" }, "b");

    fireEvent.click(screen.getByRole("radio", { name: "Summaries follows the default model" }));

    expect(onRoute).toHaveBeenCalledWith({ defaultModelId: "b", assignments: { Tags: "a" } });
  });

  it("clears the platform default from the No model row without touching the assignments", () => {
    const onRoute = show({ Tags: "a" }, "b");

    fireEvent.click(screen.getByRole("radio", { name: /use the server environment/i }));

    expect(onRoute).toHaveBeenCalledWith({ defaultModelId: null, assignments: { Tags: "a" } });
  });

  it("puts exactly one selection in every column", () => {
    // The footer promises this in prose; nothing else checks it.
    show({ Tags: "a" }, "b");

    const checked = screen.getAllByRole("radio", { checked: true });
    expect(checked).toHaveLength(7);
  });

  it("offers each model for editing", () => {
    const onEdit = vi.fn();
    render(
      <RoutingMatrix
        models={MODELS}
        assignments={{}}
        defaultModelId={null}
        onRoute={vi.fn()}
        onEdit={onEdit}
        tests={{}}
        onTest={vi.fn()}
        onTestAll={vi.fn()}
        onChatEnabledChange={vi.fn()}
      />,
    );

    fireEvent.click(screen.getAllByRole("button", { name: /edit/i })[1]);

    expect(onEdit).toHaveBeenCalledWith(MODELS[1]);
  });

  it("offers each model a connection test", () => {
    const onTest = vi.fn();
    render(
      <RoutingMatrix
        models={MODELS}
        assignments={{}}
        defaultModelId={null}
        onRoute={vi.fn()}
        onEdit={vi.fn()}
        tests={{}}
        onTest={onTest}
        onTestAll={vi.fn()}
        onChatEnabledChange={vi.fn()}
      />,
    );

    fireEvent.click(screen.getAllByRole("button", { name: /^test$/i })[1]);

    expect(onTest).toHaveBeenCalledWith(MODELS[1]);
  });

  it("reports a reachable model on its own row", () => {
    render(
      <RoutingMatrix
        models={MODELS}
        assignments={{}}
        defaultModelId={null}
        onRoute={vi.fn()}
        onEdit={vi.fn()}
        tests={{
          b: {
            status: "done",
            result: {
              ok: true, httpStatus: 200, ttftMs: 310, durationMs: 1420,
              promptTokens: 10, completionTokens: 44, reasoningTokens: null, totalTokens: 54,
              finishReason: "stop", response: "hi", requestBodyJson: "{}",
              errorKind: null, message: null, offendingParameter: null,
            },
          },
        }}
        onTest={vi.fn()}
        onTestAll={vi.fn()}
        onChatEnabledChange={vi.fn()}
      />,
    );

    // Completion tokens over duration, the same arithmetic the result card uses.
    expect(screen.getByText(/1\.42 s/)).toBeTruthy();
    expect(screen.getByText(/31\.0/)).toBeTruthy();
  });

  it("says why an unreachable model failed rather than only that it did", () => {
    render(
      <RoutingMatrix
        models={MODELS}
        assignments={{}}
        defaultModelId={null}
        onRoute={vi.fn()}
        onEdit={vi.fn()}
        tests={{
          a: {
            status: "done",
            result: {
              ok: false, httpStatus: null, ttftMs: null, durationMs: 30,
              promptTokens: null, completionTokens: null, reasoningTokens: null, totalTokens: null,
              finishReason: null, response: null, requestBodyJson: "{}",
              errorKind: "Transport", message: "No connection could be made", offendingParameter: null,
            },
          },
        }}
        onTest={vi.fn()}
        onTestAll={vi.fn()}
        onChatEnabledChange={vi.fn()}
      />,
    );

    expect(screen.getByText(/no connection could be made/i)).toBeTruthy();
  });

  // ---- Display names and the In-chat column ----

  it("leads with the display name and keeps the slug beneath it", () => {
    // The slug is what the endpoint needs and the administrator still has to be able to read it; the
    // label is what everyone else sees, so the row has to carry both.
    show({}, null, vi.fn(), [{ ...MODELS[1], displayName: "QWEN 3.8" }]);

    expect(screen.getByText("QWEN 3.8")).toBeTruthy();
    // The slug lives in the subtitle line, alongside the endpoint and context length.
    expect(screen.getByText(/^qwen3-27b · http/)).toBeTruthy();
  });

  it("shows only the slug when no display name is set", () => {
    show({}, null, vi.fn(), [MODELS[1]]);

    expect(screen.getByText("qwen3-27b")).toBeTruthy();
  });

  it("toggles whether a model is offered in chat", () => {
    const onChatEnabledChange = vi.fn();
    show({}, null, vi.fn(), MODELS, onChatEnabledChange);

    fireEvent.click(screen.getByRole("checkbox", { name: /qwen3-27b/i }));

    expect(onChatEnabledChange).toHaveBeenCalledWith("b", true);
  });

  it("un-ticks a model that is currently offered", () => {
    const onChatEnabledChange = vi.fn();
    show({}, null, vi.fn(), [{ ...MODELS[1], chatEnabled: true }], onChatEnabledChange);

    fireEvent.click(screen.getByRole("checkbox", { name: /qwen3-27b/i }));

    expect(onChatEnabledChange).toHaveBeenCalledWith("b", false);
  });

  it("ticks and locks the chat model, so the picker can never exclude it", () => {
    // The chat model is the one actually serving the conversation. An administrator who could un-offer it
    // would leave the picker unable to show the current selection at all.
    show({ Chat: "a" }, null);

    const box = screen.getByRole("checkbox", { name: /gpt-oss-20b/i }) as HTMLInputElement;
    expect(box.checked).toBe(true);
    expect(box.disabled).toBe(true);
  });

  it("locks the platform default when no model is assigned to chat", () => {
    // With no Chat assignment the default IS the chat model, so it is the one that must stay offered.
    show({}, "a");

    expect((screen.getByRole("checkbox", { name: /gpt-oss-20b/i }) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole("checkbox", { name: /qwen3-27b/i }) as HTMLInputElement).disabled).toBe(false);
  });

  it("does not report a click on the locked checkbox", async () => {
    // userEvent, not fireEvent: fireEvent dispatches straight at the node and so fires onChange even on a
    // disabled input, which would make this pass for a reason the browser never reproduces.
    const onChatEnabledChange = vi.fn();
    show({ Chat: "a" }, null, vi.fn(), MODELS, onChatEnabledChange);

    await userEvent.click(screen.getByRole("checkbox", { name: /gpt-oss-20b/i }), {
      pointerEventsCheck: 0,
    });

    expect(onChatEnabledChange).not.toHaveBeenCalled();
  });

  it("gives the No model row no in-chat checkbox", () => {
    // That row means "one level up from this column"; there is no model there to offer.
    show({}, null);

    expect(screen.getAllByRole("checkbox")).toHaveLength(MODELS.length);
  });
});
