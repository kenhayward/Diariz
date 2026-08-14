import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import RecordingTags from "./RecordingTags";

vi.mock("../../lib/api", () => ({
  api: {
    addRecordingTag: vi.fn().mockResolvedValue(undefined),
    removeRecordingTag: vi.fn().mockResolvedValue(undefined),
    dismissRecordingTag: vi.fn().mockResolvedValue(undefined),
  },
}));

import { api } from "../../lib/api";

function renderTags(props: Partial<ComponentProps<typeof RecordingTags>> = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <RecordingTags recordingId="rec-1" tags={["metadata"]} suggested={["templates"]} {...props} />
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
});
