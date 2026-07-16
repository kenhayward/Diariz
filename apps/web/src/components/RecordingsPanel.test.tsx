import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { SelectionProvider } from "../lib/selection";
import type { RecordingSummary } from "../lib/types";

vi.mock("../lib/signalr", () => ({
  createHub: () => ({ start: () => Promise.resolve(), stop: () => Promise.resolve(), on: () => {} }),
}));
// The panel scopes its lists to the current room; stub the personal room (full features shown).
const roomStub = {
  currentRoom: { id: "p1", isPersonal: true } as { id: string; isPersonal: boolean },
  canManageContents: true,
};
vi.mock("../lib/rooms", () => ({
  useRoom: () => ({ ...roomStub, can: () => roomStub.canManageContents }),
  useRoomBasePath: () =>
    roomStub.currentRoom && !roomStub.currentRoom.isPersonal ? `/rooms/${roomStub.currentRoom.id}` : "",
  useSharedRoomId: () =>
    roomStub.currentRoom && !roomStub.currentRoom.isPersonal ? roomStub.currentRoom.id : undefined,
}));

vi.mock("../lib/api", () => ({
  api: {
    listRecordings: vi.fn(),
    listSections: vi.fn().mockResolvedValue([]),
    createSection: vi.fn(),
    reorderRecordings: vi.fn(),
    retranscribe: vi.fn(),
    summarize: vi.fn(),
    deleteRecording: vi.fn(),
    deleteAudio: vi.fn(),
    deleteAudioBulk: vi.fn(),
    mergeRecordings: vi.fn(),
    renameRecording: vi.fn(),
    moveRecording: vi.fn(),
    renameSection: vi.fn(),
    deleteSection: vi.fn(),
    reorderSections: vi.fn(),
    audioUrl: vi.fn(),
    downloadTranscript: vi.fn(),
    downloadAudio: vi.fn(),
    extractActions: vi.fn(),
    reidentify: vi.fn(),
    emailTranscript: vi.fn(),
    listAllActions: vi.fn().mockResolvedValue([]),
    listTags: vi.fn().mockResolvedValue([]),
    completeActions: vi.fn().mockResolvedValue(undefined),
    getProfile: vi.fn().mockResolvedValue(null), // Calendar overlay disabled unless googleCalendar is set
    getCalendarEvents: vi.fn().mockResolvedValue([]),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import RecordingsPanel from "./RecordingsPanel";

const rec: RecordingSummary = {
  id: "rec-1",
  title: "Mic 6/26/2026",
  name: "Weekly Standup",
  source: "System",
  durationMs: 9000,
  status: "Transcribed",
  createdAt: new Date("2026-06-26T12:00:00Z").toISOString(),
  sectionId: null,
  sectionName: null,
  hasActions: false,
  hasAudio: true,
  calendarEventId: null,
};

/// `entry` seeds the URL: the drill position lives in `?in=<sectionId>`, so a test that starts inside a
/// folder just starts at that URL.
function renderList(entry = "/") {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <SelectionProvider>
        <MemoryRouter initialEntries={[entry]}>
          <RecordingsPanel />
        </MemoryRouter>
      </SelectionProvider>
    </QueryClientProvider>,
  );
}

/// Open a recording row's kebab. Its aria-label is "Actions" (KebabMenu's default) which now also matches
/// the "Actions" tab button — disambiguate by the kebab's aria-haspopup="menu".
function openKebab() {
  const btn = screen
    .getAllByRole("button", { name: /actions/i })
    .find((b) => b.getAttribute("aria-haspopup") === "menu");
  fireEvent.click(btn!);
}

describe("RecordingsPanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear(); // collapse state persists to localStorage
    roomStub.currentRoom = { id: "p1", isPersonal: true };
    roomStub.canManageContents = true;
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([]); // reset between tests (impl persists)
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([rec]);
    (api.summarize as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.deleteRecording as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("browses the current room: fetches its recordings and keeps Actions/Tags (room-scoped) in a shared room", async () => {
    roomStub.currentRoom = { id: "eng-room", isPersonal: false };
    renderList();

    expect(await screen.findByText("Weekly Standup")).toBeTruthy();
    // The list is fetched for the shared room.
    expect(api.listRecordings).toHaveBeenCalledWith("eng-room");
    // Actions + Tags now show in a shared room too, scoped to that room's shared recordings.
    expect(screen.getByRole("button", { name: /^tags$/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Actions", pressed: false })).toBeTruthy();
  });

  it("shows New section in a shared room when the caller can manage its contents, hides it otherwise", async () => {
    roomStub.currentRoom = { id: "eng-room", isPersonal: false };
    roomStub.canManageContents = true;
    const { unmount } = renderList();
    expect(await screen.findByRole("button", { name: /new section/i })).toBeTruthy();
    unmount();

    roomStub.canManageContents = false;
    renderList();
    await screen.findByText("Weekly Standup");
    expect(screen.queryByRole("button", { name: /new section/i })).toBeNull();
  });

  it("creates a section scoped to the current shared room", async () => {
    roomStub.currentRoom = { id: "eng-room", isPersonal: false };
    renderList();
    fireEvent.click(await screen.findByRole("button", { name: /new section/i }));
    fireEvent.change(screen.getByPlaceholderText(/section name/i), { target: { value: "Topics" } });
    fireEvent.submit(screen.getByPlaceholderText(/section name/i));
    await waitFor(() => expect(api.createSection).toHaveBeenCalledWith("Topics", null, "eng-room"));
  });

  it("scopes the Actions + Tags tabs to a shared room's recordings", async () => {
    roomStub.currentRoom = { id: "eng-room", isPersonal: false };
    renderList();
    await screen.findByText("Weekly Standup");

    fireEvent.click(screen.getByRole("button", { name: "Actions", pressed: false }));
    await waitFor(() => expect(api.listAllActions).toHaveBeenCalledWith("eng-room"));

    fireEvent.click(screen.getByRole("button", { name: /^tags$/i }));
    await waitFor(() => expect(api.listTags).toHaveBeenCalledWith("eng-room"));
  });

  it("keeps Actions + Tags owner-scoped (no roomId) in the personal room", async () => {
    roomStub.currentRoom = { id: "p1", isPersonal: true };
    renderList();
    await screen.findByText("Weekly Standup");

    fireEvent.click(screen.getByRole("button", { name: "Actions", pressed: false }));
    await waitFor(() => expect(api.listAllActions).toHaveBeenCalledWith(undefined));
  });

  it("keeps the room in a recording link so opening one stays in the shared room", async () => {
    roomStub.currentRoom = { id: "eng-room", isPersonal: false };
    renderList();
    const link = await screen.findByRole("link", { name: /Weekly Standup/i });
    expect(link.getAttribute("href")).toBe("/rooms/eng-room/recordings/rec-1");
  });

  it("links a personal recording to the top-level route (no room prefix)", async () => {
    roomStub.currentRoom = { id: "p1", isPersonal: true };
    renderList();
    const link = await screen.findByRole("link", { name: /Weekly Standup/i });
    expect(link.getAttribute("href")).toBe("/recordings/rec-1");
  });

  // The drill-in list shows one level at a time. These replace the old collapse/expand tests: there is no
  // collapse any more, because a folder's contents are a level you push into rather than a fold-out.
  describe("drill-in", () => {
    const drillSections = [
      { id: "customers", name: "Customers", parentId: null, position: 0 },
      { id: "ambu", name: "Ambu", parentId: "customers", position: 0 },
    ];
    const drillRecordings = [
      { ...rec, id: "root-r", name: "Loose one", sectionId: null, sectionName: null },
      { ...rec, id: "cust-r", name: "Account review", sectionId: "customers", sectionName: "Customers" },
      { ...rec, id: "ambu-r", name: "Deep in ambu", sectionId: "ambu", sectionName: "Ambu" },
    ];
    beforeEach(() => {
      (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue(drillSections);
      (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue(drillRecordings);
    });

    // The point of the redesign: the list never grows past one level, however deep the tree is.
    it("shows only the current level - not recordings nested deeper", async () => {
      renderList();
      expect(await screen.findByText("Loose one")).toBeTruthy(); // ungrouped, directly at root
      expect(screen.getByText("Customers")).toBeTruthy(); // a folder row
      expect(screen.queryByText("Account review")).toBeNull();
      expect(screen.queryByText("Deep in ambu")).toBeNull();
    });

    it("drills into a folder when its row body is clicked", async () => {
      renderList();
      fireEvent.click(await screen.findByRole("button", { name: /open customers/i }));

      expect(await screen.findByText("Account review")).toBeTruthy();
      expect(screen.getByText("Ambu")).toBeTruthy(); // the sub-folder, as a row
      expect(screen.queryByText("Loose one")).toBeNull(); // the root level is gone
      expect(screen.queryByText("Deep in ambu")).toBeNull(); // still one level down
    });

    it("drills two levels deep", async () => {
      renderList("/?in=ambu");
      expect(await screen.findByText("Deep in ambu")).toBeTruthy();
      expect(screen.queryByText("Account review")).toBeNull();
    });

    // The two targets the design insists stay distinct: the row browses, the link opens the page. The
    // link keeps `?in=` so opening the page leaves you where you were browsing.
    it("opens the folder page from the breadcrumb link, not by drilling", async () => {
      renderList("/?in=customers");
      const link = await screen.findByRole("link", { name: /open section page/i });
      expect(link.getAttribute("href")).toBe("/sections/customers?in=customers");
    });

    it("pops a level from the breadcrumb back button", async () => {
      renderList("/?in=ambu");
      fireEvent.click(await screen.findByRole("button", { name: /^back$/i }));
      expect(await screen.findByText("Account review")).toBeTruthy();
    });

    // Opening a recording must not throw away where you were browsing. Every link in the panel has to
    // carry `?in=` across, or the list pops back to the root behind the recording you just opened.
    it("keeps the drill position when opening a recording", async () => {
      renderList("/?in=customers");
      const link = await screen.findByRole("link", { name: /account review/i });
      expect(link.getAttribute("href")).toBe("/recordings/cust-r?in=customers");
    });

    it("labels the recordings filed directly in the current folder", async () => {
      renderList("/?in=customers");
      expect(await screen.findByText(/directly in customers/i)).toBeTruthy();
    });

    // Ungrouped stops being a special case: the root is just a level whose direct items happen to be the
    // recordings with no folder.
    it("shows ungrouped recordings as the root level's own items", async () => {
      renderList();
      expect(await screen.findByText("Loose one")).toBeTruthy();
      expect(screen.queryByRole("heading", { name: "Ungrouped" })).toBeNull();
    });

    it("counts recordings underneath a folder, including its sub-folders", async () => {
      renderList();
      // Customers holds one directly + one in Ambu.
      expect(await screen.findByText("2")).toBeTruthy();
    });

    // "This folder is empty" is about the folder you're in; an empty *library* still gets the
    // "No recordings yet" prompt, which tells you what to do about it.
    it("says a drilled-into folder is empty while the library has recordings elsewhere", async () => {
      (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
        { id: "empty", name: "Empty Group", parentId: null, position: 0 },
      ]);
      (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([rec]); // ungrouped, at the root
      renderList("/?in=empty");
      expect(await screen.findByText(/this folder is empty/i)).toBeTruthy();
    });

    it("keeps the New recordings prompt for an empty library", async () => {
      (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([]);
      (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([]);
      renderList();
      expect(await screen.findByText(/no recordings yet/i)).toBeTruthy();
    });
  });

  it("shows the name on the row and moves source · date into the hover title", async () => {
    renderList();
    const link = await screen.findByRole("link", { name: /weekly standup/i });
    // Source + date are no longer a visible second line — they live in the row's hover tooltip.
    expect(link.getAttribute("title")).toMatch(/System audio/);
    expect(screen.queryByText(/System audio/)).toBeNull();
  });

  it("Summarise action calls the API", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    openKebab();
    fireEvent.click(screen.getByRole("menuitem", { name: /summarise/i }));
    await waitFor(() => expect(api.summarize).toHaveBeenCalledWith("rec-1"));
  });

  it("Delete action confirms then calls the API", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    renderList();
    await screen.findByText("Weekly Standup");
    openKebab();
    fireEvent.click(screen.getByRole("menuitem", { name: "Delete" })); // exact: not "Delete audio"
    await waitFor(() => expect(api.deleteRecording).toHaveBeenCalledWith("rec-1"));
  });

  it("shows a green mic when audio is present and grey once deleted", async () => {
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "Has audio", hasAudio: true },
      { ...rec, id: "b", name: "No audio", hasAudio: false },
    ]);
    renderList();
    await screen.findByText("Has audio");
    expect(screen.getByLabelText("Audio available")).toBeTruthy();
    expect(screen.getByLabelText("Audio deleted")).toBeTruthy();
  });

  it("shows a calendar icon on a row linked to a meeting (tinted its calendar colour), and none when unlinked", async () => {
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "Linked", calendarEventId: "evt1", calendarColor: "#0B8043" },
      { ...rec, id: "b", name: "Unlinked", calendarEventId: null },
    ]);
    renderList();
    await screen.findByText("Linked");
    // Exactly one calendar icon - on the linked row - tinted the calendar's colour (#0B8043 → rgb).
    const icon = screen.getByLabelText("Linked to a calendar event");
    expect(icon.style.color).toBe("rgb(11, 128, 67)");
  });

  it("Delete audio (kebab) confirms then calls the API", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.deleteAudio as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderList();
    await screen.findByText("Weekly Standup");
    openKebab();
    fireEvent.click(screen.getByRole("menuitem", { name: "Delete audio" }));
    await waitFor(() => expect(api.deleteAudio).toHaveBeenCalledWith("rec-1"));
  });

  it("bulk Delete audio in select mode calls the API with the selected ids", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.deleteAudioBulk as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderList();
    await screen.findByText("Weekly Standup");
    fireEvent.click(screen.getByRole("button", { name: /select recordings/i })); // enter select mode
    fireEvent.click(screen.getByRole("checkbox", { name: /weekly standup/i }));   // select the row
    fireEvent.click(screen.getByRole("button", { name: "Delete audio" }));        // toolbar bulk action
    await waitFor(() => expect(api.deleteAudioBulk).toHaveBeenCalledWith(["rec-1"]));
  });

  // Was "groups recordings under section headings with Ungrouped last". The drill-in list has no headings
  // and no Ungrouped group - folders are rows you push into, and loose recordings are the root's own
  // items, listed after the folder rows. See the "drill-in" block for the replacement coverage.
  it("lists folders first, then the recordings filed at this level", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Work", parentId: null, position: 0 },
    ]);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "Grouped one", sectionId: "sec-1", sectionName: "Work" },
      { ...rec, id: "b", name: "Loose one", sectionId: null, sectionName: null },
    ]);
    renderList();

    const work = await screen.findByRole("button", { name: /open work/i });
    const loose = screen.getByText("Loose one");
    expect(work.compareDocumentPosition(loose) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("creates a section from the New section control", async () => {
    (api.createSection as ReturnType<typeof vi.fn>).mockResolvedValue({ id: "sec-9", name: "Therapy" });
    renderList();
    await screen.findByText("Weekly Standup");

    fireEvent.click(screen.getByRole("button", { name: /new section/i }));
    fireEvent.change(screen.getByPlaceholderText(/new section name/i), { target: { value: "Therapy" } });
    fireEvent.click(screen.getByRole("button", { name: /^create$/i }));

    await waitFor(() => expect(api.createSection).toHaveBeenCalledWith("Therapy", null, undefined));
  });

  it("shows a folder row even when it has no recordings yet", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([{ id: "sec-1", name: "Empty Group" }]);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([rec]); // rec is ungrouped

    renderList();
    expect(await screen.findByRole("button", { name: /open empty group/i })).toBeTruthy();
    // ...and the loose recording still lists at the root alongside it.
    expect(screen.getByText("Weekly Standup")).toBeTruthy();
  });

  it("deletes a section from its folder-row menu (recordings fall back to Ungrouped)", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.deleteSection as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Work", parentId: null, position: 0 },
    ]);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", sectionId: "sec-1", sectionName: "Work" },
      { ...rec, id: "b", sectionId: null, sectionName: null },
    ]);
    renderList();
    await screen.findByRole("button", { name: /open work/i });

    fireEvent.click(screen.getByRole("button", { name: /section actions/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /delete/i }));

    await waitFor(() => expect(api.deleteSection).toHaveBeenCalledWith("sec-1"));
  });

  it("kebab includes Re-transcribe, Summarise and Move, with a single Download transcript", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    openKebab();

    expect(screen.getByRole("menuitem", { name: /re-transcribe/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /summarise/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /move to section/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /^download transcript$/i })).toBeTruthy();
    expect(screen.queryByRole("menuitem", { name: /\.srt/i })).toBeNull();
  });

  it("kebab also offers Extract actions, Re-identify and Email (parity with the detail menu)", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    openKebab();

    expect(screen.getByRole("menuitem", { name: /extract actions/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /re-identify speakers/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /email me the transcript/i })).toBeTruthy();
  });

  it("shows the duration as m:ss", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    expect(screen.getByText("0:09")).toBeTruthy(); // 9000 ms
  });

  it("Extract actions confirms before replacing when the recording already has actions", async () => {
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([{ ...rec, hasActions: true }]);
    (api.extractActions as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    renderList();
    await screen.findByText("Weekly Standup");

    openKebab();
    fireEvent.click(screen.getByRole("menuitem", { name: /extract actions/i }));

    expect(confirm).toHaveBeenCalled();
    expect(api.extractActions).not.toHaveBeenCalled(); // declined
  });

  it("Actions tab lists cross-meeting actions; picking one and Mark complete calls the API", async () => {
    (api.listAllActions as ReturnType<typeof vi.fn>).mockResolvedValue([
      {
        id: "act-1", recordingId: "rec-1", recordingName: "Weekly Standup", text: "Send the report",
        actor: "Bob", deadline: "Fri", ordinal: 0, completed: false, completedAt: null,
        createdAt: new Date().toISOString(),
      },
    ]);
    renderList();
    await screen.findByText("Weekly Standup"); // wait for the panel to finish loading (past the spinner)
    // Switch to the Actions tab (the vertical tab carries aria-pressed; the row kebab is also "Actions").
    fireEvent.click(screen.getByRole("button", { name: "Actions", pressed: false }));
    expect(await screen.findByText("Send the report")).toBeTruthy();

    // Not in select mode: clicking the row (its recording name, not the title link) selects that one action.
    fireEvent.click(screen.getByText("Weekly Standup"));
    fireEvent.click(screen.getByRole("button", { name: /mark complete/i }));
    await waitFor(() => expect(api.completeActions).toHaveBeenCalledWith(["act-1"], true));
  });

  it("Actions tab shows the empty state when there are no actions", async () => {
    (api.listAllActions as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    renderList();
    await screen.findByText("Weekly Standup");
    fireEvent.click(screen.getByRole("button", { name: "Actions", pressed: false }));
    expect(await screen.findByText(/no action items yet/i)).toBeTruthy();
  });

  it("hides the status pill for settled states but shows in-flight ones", async () => {
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "done", name: "Finished", status: "Summarized" },
      { ...rec, id: "busy", name: "Working", status: "Summarizing" },
      { ...rec, id: "bad", name: "Broken", status: "Failed" },
    ]);
    renderList();
    await screen.findByText("Finished");

    expect(screen.queryByText("Summarized")).toBeNull(); // settled → no pill
    expect(screen.getByText("Summarizing")).toBeTruthy(); // in-flight → pill shown
    expect(screen.getByText("Failed")).toBeTruthy(); // failures still surface
  });

  it("toggles Select mode to reveal selection checkboxes", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    expect(screen.queryByRole("checkbox")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /select recordings/i }));

    expect(screen.getByRole("checkbox", { name: /select weekly standup/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /done selecting/i })).toBeTruthy();
  });

  it("renders the top controls as icon buttons with hover text", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    for (const name of ["New section", "Select recordings"]) {
      const btn = screen.getByRole("button", { name });
      expect(btn.getAttribute("title")).toBe(name);
      expect(btn.querySelector("svg")).toBeTruthy();
    }
  });

  it("shows each folder's recording count on its row", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Work", parentId: null, position: 0 },
    ]);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "Grouped", sectionId: "sec-1", sectionName: "Work" },
      { ...rec, id: "b", name: "Loose", sectionId: null, sectionName: null },
    ]);
    renderList();
    const work = await screen.findByRole("button", { name: /open work/i });
    expect(work.textContent).toContain("1");
  });

  // Was "selects every recording in a group from its header checkbox". The select-all moved from the
  // group header to the "directly in ..." label, because that is where a level's own recordings are: at
  // the root you can no longer see a folder's recordings, so a select-all on the folder row would be
  // selecting rows that aren't on screen. Drill in and the capability is unchanged.
  it("selects every recording at the current level from the select-all checkbox", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Work", parentId: null, position: 0 },
    ]);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "Alpha", sectionId: "sec-1", sectionName: "Work" },
      { ...rec, id: "b", name: "Bravo", sectionId: "sec-1", sectionName: "Work" },
    ]);
    renderList("/?in=sec-1");
    await screen.findByText("Alpha");

    fireEvent.click(screen.getByRole("button", { name: /select recordings/i }));
    fireEvent.click(screen.getByRole("checkbox", { name: /select all in work/i }));

    expect((screen.getByRole("checkbox", { name: /select alpha/i }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole("checkbox", { name: /select bravo/i }) as HTMLInputElement).checked).toBe(true);
  });

  it("reaches a sub-section and its recordings by drilling through the parent", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "cust", name: "Customers", parentId: null, position: 0 },
      { id: "acme", name: "Acme", parentId: "cust", position: 0 },
    ]);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "r1", name: "Acme call", sectionId: "acme", sectionName: "Acme" },
    ]);
    renderList();

    // Root: only the parent folder. The sub-folder and its recording are a level down.
    fireEvent.click(await screen.findByRole("button", { name: /open customers/i }));
    // Inside Customers: the sub-folder row, still not its recording.
    fireEvent.click(await screen.findByRole("button", { name: /open acme/i }));
    expect(await screen.findByText("Acme call")).toBeTruthy();
  });

  it("offers a New sub-section action on a top-level folder only", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "cust", name: "Customers", parentId: null, position: 0 },
      { id: "acme", name: "Acme", parentId: "cust", position: 0 },
    ]);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([]);

    // A top-level folder's menu offers New sub-section...
    const { unmount } = renderList();
    await screen.findByRole("button", { name: /open customers/i });
    fireEvent.click(screen.getByRole("button", { name: /section actions/i }));
    expect(screen.getByRole("menuitem", { name: /new sub-section/i })).toBeTruthy();
    unmount();

    // ...but a sub-folder's does not, since the hierarchy is capped at two levels.
    renderList("/?in=cust");
    await screen.findByRole("button", { name: /open acme/i });
    fireEvent.click(screen.getByRole("button", { name: /section actions/i }));
    expect(screen.queryByRole("menuitem", { name: /new sub-section/i })).toBeNull();
  });

  it("Merge transcripts is enabled only for 2+ and calls the API with the selection", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.mergeRecordings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "First" },
      { ...rec, id: "b", name: "Second" },
    ]);
    renderList();
    await screen.findByText("First");
    fireEvent.click(screen.getByRole("button", { name: /select recordings/i }));

    const mergeBtn = () => screen.getByRole("button", { name: /merge transcripts/i }) as HTMLButtonElement;
    expect(mergeBtn().disabled).toBe(true); // nothing selected

    fireEvent.click(screen.getByRole("checkbox", { name: /select first/i }));
    expect(mergeBtn().disabled).toBe(true); // only one

    fireEvent.click(screen.getByRole("checkbox", { name: /select second/i }));
    expect(mergeBtn().disabled).toBe(false); // two

    fireEvent.click(mergeBtn());
    await waitFor(() => expect(api.mergeRecordings).toHaveBeenCalledWith(["a", "b"]));
  });

  it("Calendar tab shows the selected day's recordings and disables list-only toolbar buttons", async () => {
    localStorage.setItem("diariz.recordings.tab", "calendar");
    const today = new Date();
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "today", name: "Today call", createdAt: today.toISOString() },
    ]);
    renderList();

    // Today is selected by default, so its recording shows in the day list.
    expect(await screen.findByText("Today call")).toBeTruthy();
    // List-only toolbar actions are disabled in Calendar; Refresh stays usable.
    expect((screen.getByRole("button", { name: /new section/i }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: /select recordings/i }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: /refresh/i }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("shows only recordings on the calendar in a shared room (no Google-event overlay)", async () => {
    roomStub.currentRoom = { id: "eng-room", isPersonal: false };
    localStorage.setItem("diariz.recordings.tab", "calendar");
    const today = new Date();
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ googleCalendar: true }); // connected
    (api.getCalendarEvents as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "e1", summary: "Standup", start: today.toISOString(), end: today.toISOString() },
    ]);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "today", name: "Today call", createdAt: today.toISOString() },
    ]);
    renderList();

    expect(await screen.findByText("Today call")).toBeTruthy(); // the room's recording shows
    // The personal Google overlay is never fetched or offered in a shared room.
    expect(api.getCalendarEvents).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: /refresh events/i })).toBeNull();
  });

  it("Tags tab shows the cloud; picking a tag filters the list below", async () => {
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "Budget call" },
      { ...rec, id: "b", name: "Vendor call" },
    ]);
    (api.listTags as ReturnType<typeof vi.fn>).mockResolvedValue([
      { tag: "Budget Planning", count: 1, weight: 0.9, recordingIds: ["a"] },
      { tag: "Vendor Selection", count: 1, weight: 0.4, recordingIds: ["b"] },
    ]);
    renderList();
    await screen.findByText("Budget call");

    fireEvent.click(screen.getByRole("button", { name: "Tags", pressed: false }));
    expect(await screen.findByRole("button", { name: "Budget Planning" })).toBeTruthy();
    // No selection: both tagged recordings are listed.
    expect(screen.getByText("Vendor call")).toBeTruthy();

    // Selecting a tag filters the list to its recordings.
    fireEvent.click(screen.getByRole("button", { name: "Budget Planning" }));
    await waitFor(() => expect(screen.queryByText("Vendor call")).toBeNull());
    expect(screen.getByText("Budget call")).toBeTruthy();
  });

  it("expands the tag cloud into a modal whose selection drives the panel", async () => {
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "Budget call" },
      { ...rec, id: "b", name: "Vendor call" },
    ]);
    (api.listTags as ReturnType<typeof vi.fn>).mockResolvedValue([
      { tag: "Budget Planning", count: 1, weight: 0.9, recordingIds: ["a"] },
      { tag: "Vendor Selection", count: 1, weight: 0.4, recordingIds: ["b"] },
    ]);
    renderList();
    await screen.findByText("Budget call");
    fireEvent.click(screen.getByRole("button", { name: "Tags", pressed: false }));
    await screen.findByRole("button", { name: "Budget Planning" });

    fireEvent.click(screen.getByRole("button", { name: /expand tag cloud/i }));
    const dialog = await screen.findByRole("dialog", { name: /tag cloud/i });

    // Selecting a tag INSIDE the modal filters the panel behind it (shared selection state).
    const inModal = Array.from(dialog.querySelectorAll("button")).find((b) => b.textContent === "Budget Planning")!;
    fireEvent.click(inModal);
    fireEvent.keyDown(document, { key: "Escape" }); // close without navigating
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(screen.queryByText("Vendor call")).toBeNull(); // panel list mirrors the modal's pick
    expect(screen.getByText("Budget call")).toBeTruthy();
  });

  it("Tags tab rows show the transcript date/time and the count slider trims the cloud", async () => {
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "Budget call", createdAt: "2026-07-01T09:30:00Z" },
      { ...rec, id: "b", name: "Vendor call", createdAt: "2026-07-02T09:30:00Z" },
      { ...rec, id: "c", name: "Cloud call", createdAt: "2026-07-03T09:30:00Z" },
    ]);
    (api.listTags as ReturnType<typeof vi.fn>).mockResolvedValue([
      { tag: "Budget Planning", count: 3, weight: 0.9, recordingIds: ["a", "b", "c"] },
      { tag: "Vendor Selection", count: 2, weight: 0.6, recordingIds: ["b", "c"] },
      { tag: "Cloud Infra", count: 1, weight: 0.3, recordingIds: ["c"] },
    ]);
    localStorage.setItem("diariz.recordings.tagLimit", "10"); // show all initially
    renderList();
    await screen.findByText("Budget call");
    fireEvent.click(screen.getByRole("button", { name: "Tags", pressed: false }));
    await screen.findByRole("button", { name: "Budget Planning" });

    // Each row shows a date/time line (year proves the visible date, not just the duration).
    expect(screen.getAllByText(/2026/).length).toBeGreaterThan(0);

    // Slider trims to the most-used tag only; the rarer tags drop out of the cloud.
    const slider = screen.getByRole("slider");
    fireEvent.change(slider, { target: { value: "1" } });
    await waitFor(() => expect(screen.queryByRole("button", { name: "Cloud Infra" })).toBeNull());
    expect(screen.getByRole("button", { name: "Budget Planning" })).toBeTruthy(); // highest count kept
  });

  it("Tags tab shows the empty state when nothing is tagged", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    fireEvent.click(screen.getByRole("button", { name: "Tags", pressed: false }));
    expect(await screen.findByText(/no tagged meetings yet/i)).toBeTruthy();
  });

  it("switches from List to Calendar via the tab strip", async () => {
    localStorage.removeItem("diariz.recordings.tab"); // start on List
    renderList();
    await screen.findByText("Weekly Standup");

    fireEvent.click(screen.getByRole("button", { name: /calendar/i }));
    // The month heading (a calendar-only element) appears; the prev/next nav is present.
    expect(screen.getByRole("button", { name: /next month/i })).toBeTruthy();
  });

  it("nests a section when dropped onto a top-level section header", async () => {
    (api.reorderSections as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "cust", name: "Customers", parentId: null, position: 0 },
      { id: "loose", name: "Loose", parentId: null, position: 1 },
    ]);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    renderList();
    await screen.findByRole("button", { name: /open customers/i });

    // Drop the "Loose" folder onto the "Customers" row → Loose becomes a sub-section of Customers.
    // The whole row is the drop target (no drag handle).
    const row = screen.getByRole("button", { name: /open customers/i }).parentElement!;
    fireEvent.drop(row, {
      dataTransfer: { getData: (type: string) => (type === "application/x-diariz-section" ? "loose" : "") },
    });
    await waitFor(() => expect(api.reorderSections).toHaveBeenCalledWith("cust", ["loose"], undefined));
  });

  it("surfaces an error when merging fails", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.mergeRecordings as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("merge boom"));
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "First" },
      { ...rec, id: "b", name: "Second" },
    ]);
    renderList();
    await screen.findByText("First");
    fireEvent.click(screen.getByRole("button", { name: /select recordings/i }));
    fireEvent.click(screen.getByRole("checkbox", { name: /select first/i }));
    fireEvent.click(screen.getByRole("checkbox", { name: /select second/i }));
    fireEvent.click(screen.getByRole("button", { name: /merge transcripts/i }));

    expect(await screen.findByText(/merge boom/i)).toBeTruthy();
  });

  it("reorders within a group via drag and drop", async () => {
    (api.reorderRecordings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "First" },
      { ...rec, id: "b", name: "Second" },
    ]);
    renderList();

    const target = (await screen.findByText("First")).closest("li")!;
    // Drop "b" onto "a" → b is inserted before a within the (ungrouped) group.
    fireEvent.drop(target, { dataTransfer: { getData: () => "b" } });

    await waitFor(() => expect(api.reorderRecordings).toHaveBeenCalledWith(null, ["b", "a"], undefined));
  });

  it("reorders within a shared room's group, scoped to that room", async () => {
    roomStub.currentRoom = { id: "eng-room", isPersonal: false };
    roomStub.canManageContents = true;
    (api.reorderRecordings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "First" },
      { ...rec, id: "b", name: "Second" },
    ]);
    renderList();

    const target = (await screen.findByText("First")).closest("li")!;
    fireEvent.drop(target, { dataTransfer: { getData: () => "b" } });

    await waitFor(() => expect(api.reorderRecordings).toHaveBeenCalledWith(null, ["b", "a"], "eng-room"));
  });

  it("does not reorder in a shared room when the caller cannot manage its contents", async () => {
    roomStub.currentRoom = { id: "eng-room", isPersonal: false };
    roomStub.canManageContents = false;
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "First" },
      { ...rec, id: "b", name: "Second" },
    ]);
    renderList();

    const target = (await screen.findByText("First")).closest("li")!;
    fireEvent.drop(target, { dataTransfer: { getData: () => "b" } });
    await new Promise((r) => setTimeout(r, 0));
    expect(api.reorderRecordings).not.toHaveBeenCalled();
  });
});
