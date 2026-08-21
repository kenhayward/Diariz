import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, useLocation } from "react-router-dom";
import { useEffect } from "react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { SelectionProvider, useSelection } from "../lib/selection";
import { RoomProvider } from "../lib/rooms";
import { RoomPermission, type RoomListItem } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: {
    listRecordings: vi.fn(),
    listRooms: vi.fn(),
    getUserSettings: vi.fn(),
    updateUserSettings: vi.fn(),
    listChatModels: vi.fn(),
    screenshotThumbUrl: (r: string, sid: string) => `/thumb/${r}/${sid}`,
    getSection: vi.fn(),
    chatStream: vi.fn(),
    uploadChatAttachment: vi.fn(),
    listChatConversations: vi.fn(),
    getChatConversation: vi.fn(),
    createChatConversation: vi.fn(),
    updateChatConversation: vi.fn(),
    deleteChatConversation: vi.fn(),
    listFormulas: vi.fn(),
    runFormula: vi.fn(),
    listFormulaResults: vi.fn(),
    getFormulaResultText: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import { fromPrompt } from "../lib/formulaTemplate";
import ChatPanel from "./ChatPanel";

const sharedRoom: RoomListItem = {
  id: "room-s", name: "Engineering", kind: 1, icon: null, color: null,
  isPersonal: false, permissions: RoomPermission.CreateRecording,
};

/// Shows the current router pathname so a test can assert where a navigation landed.
function LocationProbe() {
  return <span data-testid="loc">{useLocation().pathname}</span>;
}

/// Like renderPanel but inside a real RoomProvider, so useRoomBasePath resolves to the room in the URL and
/// the chat's transcript navigation carries the /rooms/:id prefix. listRooms must be mocked by the caller.
function renderPanelInRoom(route: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <SelectionProvider>
        <MemoryRouter initialEntries={[route]}>
          <RoomProvider>
            <ChatPanel />
            <LocationProbe />
          </RoomProvider>
        </MemoryRouter>
      </SelectionProvider>
    </QueryClientProvider>,
  );
}

const rec = (id: string, title: string, status = "Transcribed") => ({
  id, title, name: null, source: "Microphone", durationMs: 1000, status,
  createdAt: "2026-01-01T00:00:00Z", sectionId: null, sectionName: null,
});

/// Seeds the shared selection (as the list's Select mode would) before ChatPanel renders.
function Seed({ ids }: { ids: string[] }) {
  const sel = useSelection();
  useEffect(() => {
    sel.set(ids);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return null;
}

function renderPanel(route = "/recordings/rec-1", seedSelected: string[] = []) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  lastClient = qc;
  return render(
    <QueryClientProvider client={qc}>
      <SelectionProvider>
        <MemoryRouter initialEntries={[route]}>
          {seedSelected.length > 0 && <Seed ids={seedSelected} />}
          <ChatPanel />
        </MemoryRouter>
      </SelectionProvider>
    </QueryClientProvider>,
  );
}

/// The QueryClient of the most recent renderPanel, so a test can invalidate exactly as the admin page does.
let lastClient: QueryClient;

const mock = (fn: unknown) => fn as ReturnType<typeof vi.fn>;

const CHAT_MODELS = [
  { id: "a", label: "GPT OSS 20B", name: "gpt-oss", contextLength: 131072, isDefault: true, supportsImages: false, supportsTools: true, description: null },
  { id: "b", label: "QWEN 3.8", name: "qwen3.8-27b@q4_k_xl", contextLength: 200000, isDefault: false, supportsImages: true, supportsTools: false, description: null },
];

describe("ChatPanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.listRecordings).mockResolvedValue([rec("rec-1", "Standup"), rec("rec-2", "Retro", "Summarized")]);
    mock(api.getUserSettings).mockResolvedValue({
      apiBase: null, model: "gpt-oss", hasApiKey: false, defaultApiBase: null, defaultModel: "gpt-oss",
      contextWindow: 131072, chatModel: "test-model", chatModelId: null,
    });
    mock(api.updateUserSettings).mockResolvedValue(undefined);
    mock(api.listChatModels).mockResolvedValue(CHAT_MODELS);
    mock(api.chatStream).mockImplementation(async (_body: any, h: any) => {
      h.onMeta?.({ model: "gpt-oss", contextUsed: 10, contextTotal: 100 });
      h.onToken("Hello ");
      h.onToken("world");
      return { model: "gpt-oss", contextUsed: 12, contextTotal: 100 };
    });
    mock(api.createChatConversation).mockResolvedValue({ id: "conv-1", title: "Standup Recap" });
    mock(api.deleteChatConversation).mockResolvedValue(undefined);
    mock(api.listChatConversations).mockResolvedValue([]);
  });

  async function ask(text: string) {
    const box = screen.getByLabelText("Chat message");
    // Wrap in act so the streaming promise chain (onMeta/onToken/finally) settles inside act.
    await act(async () => {
      fireEvent.focus(box); // focusing snapshots the inferred context (as a real user does before typing)
      fireEvent.change(box, { target: { value: text } });
      fireEvent.click(screen.getByRole("button", { name: /^send$/i }));
    });
  }

  it("shows the context dial with the configured total before any message is sent", async () => {
    renderPanel("/recordings/rec-1");
    // 0 used out of the 131,072 server default, shown inline from the start.
    expect(await screen.findByText(/0 \/ 131,072 \(0%\)/)).toBeTruthy();
  });

  it("streams a reply, defaults context to the open recording, and shows the dial", async () => {
    renderPanel("/recordings/rec-1");
    await ask("Who spoke?");

    await waitFor(() => expect(screen.getByText("world", { exact: false })).toBeTruthy());
    expect(api.chatStream).toHaveBeenCalledWith(
      expect.objectContaining({ recordingIds: ["rec-1"], messages: [{ role: "user", content: "Who spoke?" }] }),
      expect.anything(),
    );
    // The context dial appears once usage is known.
    await waitFor(() => expect(screen.getByLabelText(/Context \d+% used/)).toBeTruthy());
  });

  it("infers the shared selection (2+ ticked) as the context, no menu choice needed", async () => {
    renderPanel("/recordings/rec-1", ["rec-1", "rec-2"]);

    await ask("Compare them");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ recordingIds: ["rec-1", "rec-2"] }),
        expect.anything(),
      ),
    );
    // The pill reflects the inferred multi-selection.
    expect(screen.getByRole("button", { name: /selected transcripts/i })).toBeTruthy();
  });

  it("infers the open folder as the context (sends a section id)", async () => {
    renderPanel("/sections/sec-1");

    await ask("Summarise this folder");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ sectionId: "sec-1", recordingIds: [] }),
        expect.anything(),
      ),
    );
    expect(screen.getByRole("button", { name: /current folder/i })).toBeTruthy();
  });

  it("infers the open folder in a shared room (matches the room-scoped section route)", async () => {
    // The middle panel opens a shared-room folder at /rooms/:id/sections/:id; chat must still detect it as
    // the current folder. Before the room-aware route hooks it only matched /sections/:id and sent no context.
    renderPanel("/rooms/room-s/sections/sec-1");

    await ask("Summarise this folder");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ sectionId: "sec-1", recordingIds: [] }),
        expect.anything(),
      ),
    );
    expect(screen.getByRole("button", { name: /current folder/i })).toBeTruthy();
  });

  it("infers the open recording in a shared room (matches the room-scoped recording route)", async () => {
    renderPanel("/rooms/room-s/recordings/rec-1");

    await ask("Summarise this");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ recordingIds: ["rec-1"] }),
        expect.anything(),
      ),
    );
  });

  it("keeps a citation click inside the shared room", async () => {
    mock(api.listRooms).mockResolvedValue([sharedRoom]);
    // The assistant cites a different recording; clicking it must open that recording within the room.
    mock(api.chatStream).mockImplementation(async (_body: any, h: any) => {
      h.onMeta?.({ model: "gpt-oss", contextUsed: 10, contextTotal: 100 });
      h.onToken("See [jump](/recordings/rec-2?t=5000)");
      return { model: "gpt-oss", contextUsed: 12, contextTotal: 100 };
    });
    renderPanelInRoom("/rooms/room-s/recordings/rec-1");

    await ask("Where was that?");
    const link = await screen.findByRole("link", { name: "jump" });
    await act(async () => fireEvent.click(link));

    await waitFor(() => expect(screen.getByTestId("loc").textContent).toBe("/rooms/room-s/recordings/rec-2"));
  });

  it("reopens a restored folder conversation inside the shared room", async () => {
    mock(api.listRooms).mockResolvedValue([sharedRoom]);
    mock(api.listChatConversations).mockResolvedValue([
      { id: "conv-1", title: "Folder chat", updatedAt: "2026-01-01T00:00:00Z" },
    ]);
    mock(api.getChatConversation).mockResolvedValue({
      id: "conv-1", title: "Folder chat", messages: [],
      context: {
        sectionId: "sec-9", recordingIds: [], searchAllMeetings: false,
        includeAttachments: false, attachmentName: null, attachmentText: null,
      },
    });
    renderPanelInRoom("/rooms/room-s/recordings/rec-1");

    await act(async () => fireEvent.click(await screen.findByRole("button", { name: /saved conversations/i })));
    await act(async () => fireEvent.click(await screen.findByRole("button", { name: "Folder chat" })));

    await waitFor(() => expect(screen.getByTestId("loc").textContent).toBe("/rooms/room-s/sections/sec-9"));
  });

  it("sends no transcript context when None is chosen", async () => {
    renderPanel("/recordings/rec-1");
    fireEvent.click(await screen.findByRole("button", { name: /context:/i }));
    fireEvent.click(await screen.findByRole("menuitemradio", { name: /none/i }));

    await ask("General question");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ recordingIds: [] }),
        expect.anything(),
      ),
    );
  });

  it("refreshes the saved-conversations list after saving, so a new one appears in an open dropdown", async () => {
    mock(api.listChatConversations).mockResolvedValue([]); // initially empty
    renderPanel("/recordings/rec-1");
    await ask("Summarise");
    await waitFor(() => expect(screen.getByText("world", { exact: false })).toBeTruthy());

    // Open the dropdown (empty) and leave it open.
    await act(async () => fireEvent.click(screen.getByRole("button", { name: /saved conversations/i })));
    expect(screen.getByText(/no saved conversations/i)).toBeTruthy();

    // After saving, the server now lists the conversation.
    mock(api.listChatConversations).mockResolvedValue([
      { id: "conv-1", title: "Standup Recap", updatedAt: "2026-01-01T00:00:00Z" },
    ]);
    await act(async () => fireEvent.click(screen.getByRole("button", { name: /save conversation/i })));
    await waitFor(() => expect(api.createChatConversation).toHaveBeenCalled());

    // The still-open dropdown reflects the save without re-toggling.
    await waitFor(() => expect(screen.getByRole("button", { name: "Standup Recap" })).toBeTruthy());
  });

  it("closes the saved-conversations dropdown on an outside click", async () => {
    mock(api.listChatConversations).mockResolvedValue([
      { id: "conv-1", title: "Old Chat", updatedAt: "2026-01-01T00:00:00Z" },
    ]);
    renderPanel("/recordings/rec-1");

    await act(async () => fireEvent.click(await screen.findByRole("button", { name: /saved conversations/i })));
    expect(await screen.findByRole("button", { name: "Old Chat" })).toBeTruthy();

    fireEvent.mouseDown(document.body); // click outside the dropdown
    await waitFor(() => expect(screen.queryByRole("button", { name: "Old Chat" })).toBeNull());
  });

  it("saves the conversation then enables and performs delete", async () => {
    renderPanel("/recordings/rec-1");
    await ask("Summarise");
    await waitFor(() => expect(screen.getByText("world", { exact: false })).toBeTruthy());

    await act(async () => fireEvent.click(screen.getByRole("button", { name: /save conversation/i })));
    await waitFor(() => expect(api.createChatConversation).toHaveBeenCalled());
    expect(screen.getByText("Saved")).toBeTruthy();

    await act(async () => fireEvent.click(screen.getByRole("button", { name: /delete conversation/i })));
    await waitFor(() => expect(api.deleteChatConversation).toHaveBeenCalledWith("conv-1"));
  });

  it("saves a folder chat with its section id", async () => {
    renderPanel("/sections/sec-1");
    await ask("Summarise this folder");
    await waitFor(() => expect(screen.getByText("world", { exact: false })).toBeTruthy());

    await act(async () => fireEvent.click(screen.getByRole("button", { name: /save conversation/i })));

    await waitFor(() =>
      expect(api.createChatConversation).toHaveBeenCalledWith(
        expect.objectContaining({ context: expect.objectContaining({ sectionId: "sec-1", recordingIds: [] }) }),
      ),
    );
  });

  it("reopening a saved folder chat restores its folder context", async () => {
    mock(api.listChatConversations).mockResolvedValue([
      { id: "conv-f", title: "Folder Chat", updatedAt: "2026-01-01T00:00:00Z" },
    ]);
    mock(api.getChatConversation).mockResolvedValue({
      id: "conv-f", title: "Folder Chat", updatedAt: "2026-01-01T00:00:00Z",
      messages: [{ role: "user", content: "hi" }, { role: "assistant", content: "hey" }],
      context: { recordingIds: [], sectionId: "sec-9", attachmentName: null, attachmentText: null },
    });
    renderPanel("/recordings/rec-1"); // start somewhere else

    await act(async () => fireEvent.click(await screen.findByRole("button", { name: /saved conversations/i })));
    await act(async () => fireEvent.click(await screen.findByRole("button", { name: "Folder Chat" })));

    expect(await screen.findByRole("button", { name: /current folder/i })).toBeTruthy();
    await ask("And the action items?");
    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ sectionId: "sec-9", recordingIds: [] }),
        expect.anything(),
      ),
    );
  });

  it("clears the conversation thread with the Clear button", async () => {
    renderPanel("/recordings/rec-1");
    await ask("Tell me something");
    await waitFor(() => expect(screen.getByText("world", { exact: false })).toBeTruthy());

    await act(async () => fireEvent.click(screen.getByRole("button", { name: /clear conversation/i })));

    expect(screen.queryByText("world", { exact: false })).toBeNull();
  });

  it("attaches a file and includes its text in the request", async () => {
    mock(api.uploadChatAttachment).mockResolvedValue({ name: "spec.pdf", text: "blue widget", chars: 11 });
    const { container } = renderPanel("/recordings/rec-1");

    const fileInput = container.querySelector('input[type="file"]') as HTMLInputElement;
    const file = new File(["%PDF"], "spec.pdf", { type: "application/pdf" });
    await act(async () => fireEvent.change(fileInput, { target: { files: [file] } }));

    await waitFor(() => expect(screen.getByText("spec.pdf")).toBeTruthy());
    await ask("Does it match?");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ attachmentName: "spec.pdf", attachmentText: "blue widget" }),
        expect.anything(),
      ),
    );
  });

  describe("/formula command", () => {
    const formula = {
      id: "f1", scope: "Personal", ownerUserId: "u1", name: "Follow-up email",
      description: null, content: fromPrompt("Draft a follow-up."), context: 1, enabled: true,
      isBuiltIn: false, shared: false,
    };

    // A run row as the API really returns it: 202 Accepted with an empty body a worker fills in later.
    const row = (status: string, error: string | null = null) => ({
      id: "res-1", recordingId: "rec-1", name: "Follow-up email", status, error,
      createdByUserId: "u1", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
    });

    beforeEach(() => {
      mock(api.listFormulas).mockResolvedValue([formula]);
      mock(api.runFormula).mockResolvedValue(row("Generating"));
      mock(api.listFormulaResults).mockResolvedValue([row("Ready")]);
      mock(api.getFormulaResultText).mockResolvedValue("**Thanks for joining.**");
    });

    it("runs the matching formula on the open recording and shows the markdown result", async () => {
      renderPanel("/recordings/rec-1");
      await ask("/formula Follow-up email");

      await waitFor(() => expect(api.runFormula).toHaveBeenCalledWith("rec-1", "f1"));
      await waitFor(() => expect(screen.getByText(/Thanks for joining/)).toBeTruthy());
      expect(api.getFormulaResultText).toHaveBeenCalledWith("rec-1", "res-1");
    });

    // The run is async: it returns a "Generating" row with an empty body. Reading the text straight away
    // printed the "Ran the ... formula:" heading with nothing under it.
    it("waits for the worker instead of printing an empty result", async () => {
      mock(api.listFormulaResults)
        .mockResolvedValueOnce([row("Generating")])
        .mockResolvedValue([row("Ready")]);

      renderPanel("/recordings/rec-1");
      await ask("/formula Follow-up email");

      // It shows progress rather than a bare heading while the worker runs...
      await waitFor(() => expect(screen.getByText(/Running the "Follow-up email" formula/)).toBeTruthy());
      // ...then the body, once the row goes Ready. The wait sleeps one 2s poll interval, hence the window.
      await waitFor(() => expect(screen.getByText(/Thanks for joining/)).toBeTruthy(), { timeout: 5000 });
      // Only fetched once the row went Ready - never against the empty Generating row.
      expect(api.getFormulaResultText).toHaveBeenCalledTimes(1);
    });

    it("shows the failure reason when the run fails", async () => {
      mock(api.listFormulaResults).mockResolvedValue([row("Failed", "The LLM request timed out.")]);

      renderPanel("/recordings/rec-1");
      await ask("/formula Follow-up email");

      await waitFor(() => expect(screen.getByText(/The LLM request timed out/)).toBeTruthy());
      expect(api.getFormulaResultText).not.toHaveBeenCalled();
    });

    it("does not send the command text to the model", async () => {
      renderPanel("/recordings/rec-1");
      await ask("/formula Follow-up email");

      await waitFor(() => expect(api.runFormula).toHaveBeenCalled());
      expect(api.chatStream).not.toHaveBeenCalled();
    });

    it("shows a message and skips the lookup when no recording is open", async () => {
      renderPanel("/sections/sec-1");
      await ask("/formula Follow-up email");

      expect(await screen.findByText(/open a recording first/i)).toBeTruthy();
      expect(api.listFormulas).not.toHaveBeenCalled();
    });

    it("shows a not-found message when no formula matches the name", async () => {
      renderPanel("/recordings/rec-1");
      await ask("/formula Nonexistent");

      expect(await screen.findByText(/no formula named/i)).toBeTruthy();
      expect(api.runFormula).not.toHaveBeenCalled();
    });

    it("does not run bare /formula with no name (sends it as a normal message instead)", async () => {
      renderPanel("/recordings/rec-1");
      await ask("/formula");

      await waitFor(() => expect(api.chatStream).toHaveBeenCalled());
      expect(api.listFormulas).not.toHaveBeenCalled();
    });

    it("asks the user to be specific when several formulas match and no name is exact", async () => {
      mock(api.listFormulas).mockResolvedValue([
        formula, // "Follow-up email"
        { ...formula, id: "f2", name: "Follow-up call" },
      ]);
      renderPanel("/recordings/rec-1");
      await ask("/formula follow");

      // Both matches are listed so the user can pick one, and nothing is run.
      expect(await screen.findByText(/Follow-up email, Follow-up call/)).toBeTruthy();
      expect(api.runFormula).not.toHaveBeenCalled();
    });

    it("shows the error and stays usable when the run fails", async () => {
      mock(api.runFormula).mockRejectedValue(new Error("boom"));
      renderPanel("/recordings/rec-1");
      await ask("/formula Follow-up email");

      await waitFor(() => expect(api.runFormula).toHaveBeenCalledWith("rec-1", "f1"));
      // apiErrorMessage is mocked to String(e), so the Error's message surfaces in the command output.
      expect(await screen.findByText(/boom/)).toBeTruthy();
      // The input is still usable (not stuck streaming) - a fresh message can be typed and sent.
      const box = screen.getByLabelText("Chat message") as HTMLTextAreaElement;
      expect(box.disabled).toBe(false);
      await ask("A normal question");
      await waitFor(() => expect(api.chatStream).toHaveBeenCalled());
    });
  });

  describe("stopping a turn", () => {
    /// A turn whose endpoint accepts the request and then never streams - the shape a misconfigured
    /// endpoint produces, and the reason the panel has to be escapable rather than merely eventually
    /// timing out.
    function hangingTurn() {
      mock(api.chatStream).mockImplementation(
        (_body: any, h: any) =>
          new Promise((_resolve, reject) => {
            h.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
          }),
      );
    }

    it("offers stop in place of send while a reply is pending", async () => {
      hangingTurn();
      renderPanel("/recordings/rec-1");
      await ask("Who spoke?");

      expect(screen.getByRole("button", { name: /stop/i })).toBeTruthy();
      expect(screen.queryByRole("button", { name: /^send$/i })).toBeNull();
    });

    it("returns to send once stopped, leaving the panel usable", async () => {
      // Without this the box stays unsendable until the server-side idle timeout expires - two minutes of
      // a panel that looks broken.
      hangingTurn();
      renderPanel("/recordings/rec-1");
      await ask("Who spoke?");

      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /stop/i }));
      });

      expect(await screen.findByRole("button", { name: /^send$/i })).toBeTruthy();
      expect(screen.queryByRole("button", { name: /stop/i })).toBeNull();
    });

    it("can send again after stopping", async () => {
      hangingTurn();
      renderPanel("/recordings/rec-1");
      await ask("Who spoke?");
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /stop/i }));
      });

      mock(api.chatStream).mockResolvedValue({ model: "gpt-oss", contextUsed: 1, contextTotal: 100 });
      await ask("Second question");

      expect(mock(api.chatStream).mock.calls.length).toBe(2);
    });
  });

  describe("model picker", () => {
    async function pickQwen() {
      renderPanel("/recordings/rec-1");
      // Wait for the models to LAND, not merely for the request to go out - the button exists either way,
      // and clicking too early opens an empty menu.
      await screen.findByRole("button", { name: /GPT OSS 20B/ });
      fireEvent.click(screen.getByRole("button", { name: /model/i }));
      await act(async () => {
        fireEvent.click(screen.getByRole("menuitemradio", { name: /QWEN 3\.8/ }));
      });
    }

    it("updates the context dial as soon as a model is picked, before any turn", async () => {
      // The dial has to move on selection. Waiting for the next turn's meta event would show the previous
      // model's window for as long as the user sat there reading it.
      await pickQwen();

      expect(await screen.findByText(/0 \/ 200,000 \(0%\)/)).toBeTruthy();
      expect(api.chatStream).not.toHaveBeenCalled();
    });

    it("sends the picked model on the next turn", async () => {
      await pickQwen();
      await ask("Who spoke?");

      await waitFor(() => expect(api.chatStream).toHaveBeenCalled());
      expect(mock(api.chatStream).mock.calls[0][0].modelId).toBe("b");
    });

    it("sends the default model when the user has picked nothing", async () => {
      renderPanel("/recordings/rec-1");
      await waitFor(() => expect(api.listChatModels).toHaveBeenCalled());
      await ask("Who spoke?");

      await waitFor(() => expect(api.chatStream).toHaveBeenCalled());
      expect(mock(api.chatStream).mock.calls[0][0].modelId).toBe("a");
    });

    it("remembers the choice as a user setting", async () => {
      await pickQwen();

      await waitFor(() =>
        expect(api.updateUserSettings).toHaveBeenCalledWith(expect.objectContaining({ chatModelId: "b" })),
      );
    });

    it("starts on the model the user chose last time", async () => {
      mock(api.getUserSettings).mockResolvedValue({
        apiBase: null, model: "gpt-oss", hasApiKey: false, defaultApiBase: null, defaultModel: "gpt-oss",
        contextWindow: 200000, chatModel: "qwen3.8-27b@q4_k_xl", chatModelId: "b",
      });
      renderPanel("/recordings/rec-1");
      await waitFor(() => expect(api.listChatModels).toHaveBeenCalled());

      expect(await screen.findByText(/0 \/ 200,000 \(0%\)/)).toBeTruthy();
    });

    it("keeps the label on the dial after a turn reports the slug", async () => {
      // The stream's meta event carries the slug the endpoint needs. Rendering it raw would flip the dial
      // from "QWEN 3.8" to "qwen3.8-27b@q4_k_xl" the instant the first token arrived.
      mock(api.chatStream).mockImplementation(async (_body: any, h: any) => {
        h.onMeta?.({ model: "qwen3.8-27b@q4_k_xl", contextUsed: 10, contextTotal: 200000 });
        h.onToken("hi");
        return { model: "qwen3.8-27b@q4_k_xl", contextUsed: 12, contextTotal: 200000 };
      });
      await pickQwen();
      await ask("Who spoke?");
      await waitFor(() => expect(api.chatStream).toHaveBeenCalled());

      expect(screen.queryByText(/qwen3\.8-27b@q4_k_xl/)).toBeNull();
      expect(screen.getByText("QWEN 3.8")).toBeTruthy();
    });

    it("disables the picker while a reply is streaming", async () => {
      let release: (() => void) | null = null;
      mock(api.chatStream).mockImplementation(
        (_body: any, h: any) =>
          new Promise((resolve) => {
            h.onMeta?.({ model: "gpt-oss", contextUsed: 10, contextTotal: 100 });
            release = () => resolve({ model: "gpt-oss", contextUsed: 12, contextTotal: 100 });
          }),
      );
      renderPanel("/recordings/rec-1");
      await waitFor(() => expect(api.listChatModels).toHaveBeenCalled());
      await ask("Who spoke?");

      expect((screen.getByRole("button", { name: /model/i }) as HTMLButtonElement).disabled).toBe(true);
      await act(async () => release!());
    });

    it("reloads its models when the admin model list is invalidated", async () => {
      // The contract that keeps the picker in step with the AI models screen: this query must live UNDER
      // ["llm-models"], so every admin write reaches it by prefix. Keyed independently - as it first was -
      // an administrator could tick a model into the picker and the picker would never hear about it,
      // because the settings modal opens over the app without blurring the window, so refetch-on-focus
      // never fires either.
      renderPanel("/recordings/rec-1");
      await waitFor(() => expect(api.listChatModels).toHaveBeenCalledTimes(1));

      await act(async () => {
        await lastClient.invalidateQueries({ queryKey: ["llm-models"] });
      });

      await waitFor(() => expect(api.listChatModels).toHaveBeenCalledTimes(2));
    });

    it("saves the conversation's model and restores it on reopen", async () => {
      await pickQwen();
      await ask("Who spoke?");
      await waitFor(() => expect(api.chatStream).toHaveBeenCalled());

      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /save conversation/i }));
      });

      await waitFor(() => expect(api.createChatConversation).toHaveBeenCalled());
      expect(mock(api.createChatConversation).mock.calls[0][0].context.modelId).toBe("b");
    });
  });

  // ---- Vision: screenshots dragged into the prompt ----

  describe("screenshot attachments", () => {
    /// jsdom has no DataTransfer, so the drop is driven through a stub shaped like the real one.
    function drop(payload: Record<string, unknown> | null, type = "application/x-diariz-screenshot") {
      const data = payload === null ? "" : JSON.stringify(payload);
      fireEvent.drop(screen.getByTestId("chat-drop-zone"), {
        dataTransfer: { getData: (t: string) => (t === type ? data : ""), types: [type] },
      });
    }

    const shotA = { recordingId: "rec-1", screenshotId: "shot-a", capturedAtMs: 1000 };
    const shotB = { recordingId: "rec-1", screenshotId: "shot-b", capturedAtMs: 2000 };

    async function renderReady() {
      renderPanel();
      await waitFor(() => expect(api.listChatModels).toHaveBeenCalled());
    }

    it("adds a thumbnail when a capture is dropped on the composer", async () => {
      await renderReady();

      act(() => drop(shotA));

      await waitFor(() =>
        expect(screen.getByAltText(/attached screenshot/i).getAttribute("src")).toBe("/thumb/rec-1/shot-a"));
    });

    it("ignores a second drop of the same capture", async () => {
      await renderReady();

      act(() => drop(shotA));
      await waitFor(() => expect(screen.getAllByAltText(/attached screenshot/i)).toHaveLength(1));
      act(() => drop(shotA));

      expect(screen.getAllByAltText(/attached screenshot/i)).toHaveLength(1);
    });

    it("accepts several different captures", async () => {
      await renderReady();

      act(() => drop(shotA));
      act(() => drop(shotB));

      await waitFor(() => expect(screen.getAllByAltText(/attached screenshot/i)).toHaveLength(2));
    });

    /// A dragged word or link must not be mistaken for a capture - that is why the payload has its own
    /// MIME type rather than riding text/plain.
    it("ignores a drop that carries no capture payload", async () => {
      await renderReady();

      act(() => drop(null, "text/plain"));

      expect(screen.queryByAltText(/attached screenshot/i)).toBeNull();
    });

    it("removes a capture when its remove control is clicked", async () => {
      await renderReady();
      act(() => drop(shotA));
      await waitFor(() => expect(screen.getByAltText(/attached screenshot/i)).toBeTruthy());

      await userEvent.click(screen.getByRole("button", { name: /remove screenshot/i }));

      expect(screen.queryByAltText(/attached screenshot/i)).toBeNull();
    });

    it("refuses to send while the selected model cannot read images", async () => {
      await renderReady();
      fireEvent.change(screen.getByLabelText(/message/i), { target: { value: "what is this?" } });
      act(() => drop(shotA));
      await waitFor(() => expect(screen.getByAltText(/attached screenshot/i)).toBeTruthy());

      expect(screen.getByText(/select a vision model/i)).toBeTruthy();
      // userEvent, not fireEvent: fireEvent dispatches onto a disabled control, so the lock would appear
      // to hold for a reason the browser never reproduces.
      await userEvent.click(screen.getByRole("button", { name: /^send$/i }));

      expect(api.chatStream).not.toHaveBeenCalled();
    });

    it("sends once a vision-capable model is chosen", async () => {
      await renderReady();
      fireEvent.change(screen.getByLabelText(/message/i), { target: { value: "what is this?" } });
      act(() => drop(shotA));
      await waitFor(() => expect(screen.getByAltText(/attached screenshot/i)).toBeTruthy());

      await userEvent.click(screen.getByRole("button", { name: /^Model:/ }));
      await userEvent.click(await screen.findByRole("menuitemradio", { name: /QWEN 3\.8/ }));

      await waitFor(() => expect(screen.queryByText(/select a vision model/i)).toBeNull());
      await userEvent.click(screen.getByRole("button", { name: /^send$/i }));

      await waitFor(() => expect(api.chatStream).toHaveBeenCalled());
      expect(mock(api.chatStream).mock.calls[0][0].screenshots).toEqual([
        { recordingId: "rec-1", screenshotId: "shot-a" },
      ]);
    });

    it("sends no screenshots field when nothing is attached", async () => {
      await renderReady();
      fireEvent.change(screen.getByLabelText(/message/i), { target: { value: "hello" } });

      await userEvent.click(screen.getByRole("button", { name: /^send$/i }));

      await waitFor(() => expect(api.chatStream).toHaveBeenCalled());
      expect(mock(api.chatStream).mock.calls[0][0].screenshots).toEqual([]);
    });

    it("restores attached captures when a saved conversation is reopened", async () => {
      mock(api.listChatConversations).mockResolvedValue([
        { id: "conv-1", title: "Slide question", updatedAt: "2026-01-01T00:00:00Z" },
      ]);
      mock(api.getChatConversation).mockResolvedValue({
        id: "conv-1",
        title: "Slide question",
        messages: [{ role: "user", content: "what is on this slide?" }],
        updatedAt: "2026-01-01T00:00:00Z",
        context: {
          recordingIds: [], attachmentName: null, attachmentText: null,
          screenshots: [{ recordingId: "rec-9", screenshotId: "shot-z" }],
        },
      });
      await renderReady();

      await userEvent.click(screen.getByRole("button", { name: /saved conversations/i }));
      await userEvent.click(await screen.findByRole("button", { name: /Slide question/ }));

      await waitFor(() =>
        expect(screen.getByAltText(/attached screenshot/i).getAttribute("src")).toBe("/thumb/rec-9/shot-z"));
    });
  });
});
