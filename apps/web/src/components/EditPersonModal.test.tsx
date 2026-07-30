import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import EditPersonModal from "./EditPersonModal";
import { api } from "../lib/api";
import type { Person, PersonDetail } from "../lib/types";

vi.mock("../auth", () => ({ useAuth: () => ({ permissions: { managePeople: true } }) }));

vi.mock("../lib/api", () => ({
  api: {
    getPerson: vi.fn(),
    updatePerson: vi.fn(),
    deletePerson: vi.fn(),
    deleteVoiceprint: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

const person: Person = {
  id: "p1", name: "Lizzie Mcneil", title: "Presenter", companyName: "BBC",
  email: "lizzie@bbc.test", phone: null, isInternal: false, voiceprintOptOut: false,
  hasVoiceprint: true, sampleCount: 3, linkedUserId: null, isSelf: false,
  canManageBiometrics: true, createdAt: "2026-07-30T00:00:00Z", updatedAt: "2026-07-30T00:00:00Z",
};

const detail: PersonDetail = { person, identifiedCount: 1, samples: [] };

const onClose = vi.fn();

function render_() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <EditPersonModal personId="p1" onClose={onClose} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mock(api.getPerson).mockResolvedValue(detail);
  mock(api.updatePerson).mockResolvedValue(undefined);
});

describe("EditPersonModal", () => {
  it("loads the person behind the speaker and fills the editor", async () => {
    render_();

    expect(((await screen.findByLabelText("Name")) as HTMLInputElement).value).toBe("Lizzie Mcneil");
    expect((screen.getByLabelText("Job title") as HTMLInputElement).value).toBe("Presenter");
    expect(api.getPerson).toHaveBeenCalledWith("p1");
  });

  it("saves through the same endpoint the directory uses", async () => {
    render_();
    fireEvent.change(await screen.findByLabelText("Job title"), { target: { value: "Host" } });

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(api.updatePerson).toHaveBeenCalledWith("p1", expect.objectContaining({ title: "Host" })));
  });

  /// Half-typed edits are easy to lose and impossible to recover, so the only ways out are the two buttons.
  /// The directory modal closes on a backdrop click; this one deliberately does not.
  it("does not close when the backdrop is clicked", async () => {
    render_();
    await screen.findByLabelText("Name");

    fireEvent.click(screen.getByTestId("edit-person-backdrop"));

    expect(onClose).not.toHaveBeenCalled();
  });

  it("does not close on Escape", async () => {
    render_();
    await screen.findByLabelText("Name");

    fireEvent.keyDown(document, { key: "Escape" });

    expect(onClose).not.toHaveBeenCalled();
  });

  /// The editor already ends in Save / Cancel, which is the ok-and-cancel pair this needs - so the wrapper
  /// adds no buttons of its own rather than putting a second, competing pair around them.
  it("closes from the editor's own Cancel button", async () => {
    render_();
    await screen.findByLabelText("Name");

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(onClose).toHaveBeenCalled();
  });

  it("says so when the person cannot be loaded", async () => {
    mock(api.getPerson).mockRejectedValue(new Error("nope"));
    render_();

    expect(await screen.findByText(/Could not load/i)).toBeTruthy();
  });
});
