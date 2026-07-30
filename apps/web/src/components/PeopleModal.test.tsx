import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import PeopleModal from "./PeopleModal";
import { api } from "../lib/api";
import type { Person, PersonDuplicateGroup } from "../lib/types";

vi.mock("../auth", () => ({ useAuth: () => ({ permissions: { managePeople: true } }) }));
vi.mock("./HelpButton", () => ({ default: () => null }));

vi.mock("../lib/api", () => ({
  api: {
    listPeople: vi.fn(),
    findPersonDuplicates: vi.fn(),
    mergePeople: vi.fn(),
    updatePerson: vi.fn(),
    deletePerson: vi.fn(),
    deleteVoiceprint: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function person(id: string, name: string, over: Partial<Person> = {}): Person {
  return {
    id, name, title: null, companyName: null, email: null, phone: null,
    isInternal: false, voiceprintOptOut: false, hasVoiceprint: false, sampleCount: 0,
    linkedUserId: null, isSelf: false, canManageBiometrics: true,
    createdAt: "2026-07-29T00:00:00Z", updatedAt: "2026-07-29T00:00:00Z", ...over,
  };
}

const people = [
  person("p1", "Ada Lovelace", { isInternal: true, title: "Engineer", hasVoiceprint: true, sampleCount: 2 }),
  person("p2", "Grace Hopper", { companyName: "US Navy" }),
];

function render_(onClose = () => {}) {
  render_v(onClose);
}

function render_v(onClose = () => {}) {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <PeopleModal onClose={onClose} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mock(api.listPeople).mockResolvedValue(people);
  mock(api.findPersonDuplicates).mockResolvedValue([]);
  mock(api.mergePeople).mockResolvedValue(undefined);
});

describe("PeopleModal", () => {
  it("lists the directory", async () => {
    render_();

    expect(await screen.findByRole("button", { name: /Ada Lovelace/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /Grace Hopper/ })).toBeTruthy();
  });

  /// One line per person: the name and the voiceprint marker share a row, so a long directory stays
  /// scannable without a second line per entry.
  it("marks who has a voiceprint on the same row as the name", async () => {
    render_();

    const withPrint = await screen.findByRole("button", { name: /Ada Lovelace/ });
    const without = screen.getByRole("button", { name: /Grace Hopper/ });

    expect(withPrint.getAttribute("aria-pressed")).toBe("false");
    expect(withPrint.textContent).toContain("Ada Lovelace");
    expect(within(withPrint).queryByLabelText("Has a voiceprint")).toBeTruthy();
    expect(within(without).queryByLabelText("No voiceprint")).toBeTruthy();
  });

  it("searches the directory server-side", async () => {
    render_();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "navy" } });

    await waitFor(() =>
      expect(api.listPeople).toHaveBeenCalledWith(expect.objectContaining({ q: "navy" })));
  });

  it("filters to internal people", async () => {
    render_();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    fireEvent.click(screen.getByRole("button", { name: "Internal" }));

    await waitFor(() =>
      expect(api.listPeople).toHaveBeenCalledWith(expect.objectContaining({ isInternal: true })));
  });

  it("filters to people who have a voiceprint", async () => {
    render_();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    fireEvent.click(screen.getByRole("button", { name: "Has voiceprint" }));

    await waitFor(() =>
      expect(api.listPeople).toHaveBeenCalledWith(expect.objectContaining({ hasVoiceprint: true })));
  });

  it("opens the editor with the selected person's fields", async () => {
    render_();

    fireEvent.click(await screen.findByRole("button", { name: /Ada Lovelace/ }));

    expect(((await screen.findByLabelText("Name")) as HTMLInputElement).value).toBe("Ada Lovelace");
    expect((screen.getByLabelText("Job title") as HTMLInputElement).value).toBe("Engineer");
  });

  /// Reported, never automatic: a merge deletes the source record, cannot be undone, and in a shared
  /// directory affects everyone's recordings. The banner therefore opens the review dialog rather than
  /// merging on the spot - it takes a second, deliberate click to destroy anything.
  it("opens the review dialog for a reported duplicate rather than merging on the spot", async () => {
    const dupes: PersonDuplicateGroup[] = [{ reason: "email", people: [people[0], people[1]] }];
    mock(api.findPersonDuplicates).mockResolvedValue(dupes);
    render_();

    fireEvent.click(await screen.findByRole("button", { name: "Review and merge" }));

    expect(screen.getByRole("dialog", { name: "Merge two records" })).toBeTruthy();
    expect(api.mergePeople).not.toHaveBeenCalled();
  });

  it("merges once the review dialog is confirmed", async () => {
    const dupes: PersonDuplicateGroup[] = [{ reason: "email", people: [people[0], people[1]] }];
    mock(api.findPersonDuplicates).mockResolvedValue(dupes);
    mock(api.mergePeople).mockResolvedValue(undefined);
    render_();

    fireEvent.click(await screen.findByRole("button", { name: "Review and merge" }));
    fireEvent.click(screen.getByRole("button", { name: "Merge" }));

    await waitFor(() => expect(api.mergePeople).toHaveBeenCalledWith("p1", "p2"));
  });

  it("says nothing about duplicates when there are none", async () => {
    render_();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    expect(screen.queryByRole("button", { name: /merge/i })).toBeNull();
  });

  // ---- Modal behaviour: it exists so a transcript stays in context, so closing must be easy. ----

  it("closes from the footer button", async () => {
    const onClose = vi.fn();
    render_(onClose);
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    // Two ways out: the header cross and the footer button. The footer one is last in the DOM, and is the
    // one asked for - a modal opened over a transcript needs an obvious way back.
    const closes = screen.getAllByRole("button", { name: "Close" });
    expect(closes).toHaveLength(2);
    fireEvent.click(closes[closes.length - 1]);

    expect(onClose).toHaveBeenCalled();
  });

  it("closes on Escape", async () => {
    const onClose = vi.fn();
    render_(onClose);
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    fireEvent.keyDown(document, { key: "Escape" });

    expect(onClose).toHaveBeenCalled();
  });

  // ---- Dismissing a suggestion ----
  //
  // A suggestion you disagree with is not actionable and not removable: there is nothing to merge and nothing
  // to correct, so it sits at the top of the directory permanently. Dismissing is deliberately for this
  // sitting only - the pair really might be the same person, and a decision to hide it forever is not one to
  // take from a banner.

  it("hides a dismissed suggestion", async () => {
    mock(api.findPersonDuplicates).mockResolvedValue([
      { reason: "name", people: [people[0], people[1]] },
    ] as PersonDuplicateGroup[]);
    render_();
    await screen.findByRole("button", { name: "Review and merge" });

    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));

    expect(screen.queryByRole("button", { name: "Review and merge" })).toBeNull();
  });

  it("leaves the other suggestions in place", async () => {
    const third = person("p3", "Carol Shaw");
    mock(api.findPersonDuplicates).mockResolvedValue([
      { reason: "name", people: [people[0], people[1]] },
      { reason: "email", people: [people[1], third] },
    ] as PersonDuplicateGroup[]);
    render_();
    await screen.findByText(/Same name/);

    fireEvent.click(screen.getAllByRole("button", { name: "Dismiss" })[0]);

    expect(screen.queryByText(/Same name/)).toBeNull();
    expect(screen.getByText(/Same email address/)).toBeTruthy();
  });

  it("drops the whole banner once every suggestion is dismissed", async () => {
    mock(api.findPersonDuplicates).mockResolvedValue([
      { reason: "name", people: [people[0], people[1]] },
    ] as PersonDuplicateGroup[]);
    render_();
    await screen.findByText("Possible duplicates");

    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));

    expect(screen.queryByText("Possible duplicates")).toBeNull();
  });

  /// Reopening the directory is a fresh sitting, so the suggestion comes back. Nothing was recorded about it.
  it("brings a dismissed suggestion back when the directory is reopened", async () => {
    mock(api.findPersonDuplicates).mockResolvedValue([
      { reason: "name", people: [people[0], people[1]] },
    ] as PersonDuplicateGroup[]);
    const { unmount } = render_v();
    await screen.findByRole("button", { name: "Review and merge" });
    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    unmount();

    render_();

    expect(await screen.findByRole("button", { name: "Review and merge" })).toBeTruthy();
  });
});
