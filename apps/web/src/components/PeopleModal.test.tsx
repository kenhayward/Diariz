import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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
    getDirectoryDiagnostics: vi.fn(),
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
  mock(api.getDirectoryDiagnostics).mockResolvedValue([]);
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

  /// Two people with the same name are indistinguishable in a platform-wide directory. The row has to
  /// carry the one fact that separates them: which account each person is.
  it("shows each person's account identity in the list", async () => {
    mock(api.listPeople).mockResolvedValue([
      person("a", "Ken Hayward", { linkedUserId: "u1", isSelf: true, email: "ken@example.com" }),
      person("b", "Ken Hayward", { linkedUserId: "u2", isSelf: false, email: "ken@acme.com" }),
      person("c", "Ken Hayward"),
    ]);
    render_();

    expect(await screen.findByText("ken@example.com - your account")).toBeTruthy();
    expect(screen.getByText("ken@acme.com - Diariz account")).toBeTruthy();
    // A blank would read as "not loaded yet"; an unlinked person says so plainly.
    expect(screen.getByText("no Diariz account")).toBeTruthy();
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

    fireEvent.click(await screen.findByRole("button", { name: /Ada Lovelace/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Review and merge" }));

    expect(screen.getByRole("dialog", { name: "Merge two records" })).toBeTruthy();
    expect(api.mergePeople).not.toHaveBeenCalled();
  });


  /// Before this the notice joined bare names - "Same name: Ada Lovelace, Ada Lovelace" - which is
  /// undecidable without opening the dialog, and the pair most worth reporting is exactly the one where
  /// the names match. Each person gets their own line and their own account identity.
  it("lists each duplicate with its identity rather than joining bare names", async () => {
    const twins = [
      person("a", "Ada Lovelace", { linkedUserId: "u1", isSelf: true, email: "ada@example.com" }),
      person("b", "Ada Lovelace"),
    ];
    // The strip follows the person you opened, so the directory has to contain the pair it is about.
    mock(api.listPeople).mockResolvedValue(twins);
    mock(api.findPersonDuplicates).mockResolvedValue([
      { reason: "name", people: twins },
    ] as PersonDuplicateGroup[]);
    render_();

    fireEvent.click((await screen.findAllByRole("button", { name: /Ada Lovelace/ }))[0]);

    const strip = (await screen.findByRole("button", { name: "Review and merge" })).closest("div")!;
    expect(within(strip).getByText(/ada@example\.com - your account/)).toBeTruthy();
    expect(within(strip).getByText(/no Diariz account/)).toBeTruthy();
  });

  it("merges once the review dialog is confirmed", async () => {
    const dupes: PersonDuplicateGroup[] = [{ reason: "email", people: [people[0], people[1]] }];
    mock(api.findPersonDuplicates).mockResolvedValue(dupes);
    mock(api.mergePeople).mockResolvedValue(undefined);
    render_();

    fireEvent.click(await screen.findByRole("button", { name: /Ada Lovelace/ }));
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

  // ---- Dismissing a warning ----
  //
  // A suggestion you disagree with is not actionable and not removable: there is nothing to merge and
  // nothing to correct, so it would sit against that person permanently. Dismissing is deliberately for
  // this sitting only - the pair really might be the same person - and it is keyed on the person rather
  // than the duplicate group, so a merge elsewhere reordering the list cannot land it on the wrong row.

  it("leaves everyone else's warnings alone", async () => {
    const third = person("p3", "Carol Shaw");
    mock(api.listPeople).mockResolvedValue([...people, third]);
    mock(api.findPersonDuplicates).mockResolvedValue([
      { reason: "name", people: [people[0], third] },
    ] as PersonDuplicateGroup[]);
    render_();
    await screen.findByText("Carol Shaw");

    // Ada is the first row, so hers is the first Dismiss.
    fireEvent.click(screen.getAllByRole("button", { name: "Dismiss" })[0]);

    const rows = screen.getAllByRole("button", { name: /Possible duplicate/ });
    expect(rows).toHaveLength(1);
    expect(rows[0].textContent).toContain("Carol Shaw");
  });

  // ---- Warnings live on the row now. Reported directly: with both panels showing, the person card
  // below was pushed almost off screen and the Voiceprint tab was barely usable. ----

  const unhealthy = [{ personId: "p1", name: "Ada Lovelace", sampleCount: 5, aloneCount: 2, widestPair: 0.9 }];
  const dupes: PersonDuplicateGroup[] = [{ reason: "name", people: [people[0], people[1]] }];

  it("says nothing when there is nothing to say", async () => {
    render_();

    await screen.findByText("Ada Lovelace");
    expect(screen.queryByText(/sound different/i)).toBeNull();
    expect(screen.queryByText(/possible duplicate/i)).toBeNull();
  });

  it("puts the warnings on the person's row instead of over the whole directory", async () => {
    mock(api.getDirectoryDiagnostics).mockResolvedValue(unhealthy);
    mock(api.findPersonDuplicates).mockResolvedValue(dupes);
    render_();

    const row = (await screen.findByRole("button", { name: /Ada Lovelace/ }));
    expect(within(row).getByText(/2 of 5 recordings sound different/)).toBeTruthy();
    expect(within(row).getByText(/Possible duplicate/)).toBeTruthy();
  });

  it("can narrow the directory to the people who need a look", async () => {
    // The replacement for the ranking the panel used to give. Scanning a long directory for amber is not
    // a way to find anything.
    mock(api.getDirectoryDiagnostics).mockResolvedValue(unhealthy);
    render_();
    await screen.findByText("Grace Hopper");

    await userEvent.click(screen.getByRole("button", { name: "Needs review" }));

    expect(screen.queryByText("Grace Hopper")).toBeNull();
    expect(screen.getByText("Ada Lovelace")).toBeTruthy();
  });

  it("offers the merge once you open the person it is about", async () => {
    // The action follows the person you land on, rather than sitting above the directory where it covered
    // the card it was asking you to look at.
    mock(api.findPersonDuplicates).mockResolvedValue(dupes);
    render_();

    await userEvent.click(await screen.findByRole("button", { name: /Ada Lovelace/ }));

    expect(screen.getByRole("button", { name: "Review and merge" })).toBeTruthy();
  });

  it("dismisses a person's warnings for this sitting, and brings them back next time", async () => {
    // The pair really might be the same person and the recording really might be them on a car phone.
    // Hiding either forever is not a decision to take from one click with no undo.
    mock(api.getDirectoryDiagnostics).mockResolvedValue(unhealthy);
    const { unmount } = render_v();
    await screen.findByText(/2 of 5 recordings sound different/);

    await userEvent.click(screen.getByRole("button", { name: /dismiss/i }));
    expect(screen.queryByText(/sound different/i)).toBeNull();

    unmount();
    render_v();

    expect(await screen.findByText(/2 of 5 recordings sound different/)).toBeTruthy();
  });
});