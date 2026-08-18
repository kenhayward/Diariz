import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import type { LlmModel } from "../../lib/types";
import RoutingMatrix from "./RoutingMatrix";

const MODELS: LlmModel[] = [
  { id: "a", name: "gpt-oss-20b", apiBase: "http://a/v1", hasApiKey: true, contextLength: 8192, parameters: {} },
  { id: "b", name: "qwen3-27b", apiBase: "http://b/v1", hasApiKey: false, contextLength: 200000, parameters: {} },
];

function show(assignments: Record<string, string>, defaultModelId: string | null, onRoute = vi.fn()) {
  render(
    <RoutingMatrix
      models={MODELS}
      assignments={assignments}
      defaultModelId={defaultModelId}
      onRoute={onRoute}
      onEdit={vi.fn()}
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
      />,
    );

    fireEvent.click(screen.getAllByRole("button", { name: /edit/i })[1]);

    expect(onEdit).toHaveBeenCalledWith(MODELS[1]);
  });
});
